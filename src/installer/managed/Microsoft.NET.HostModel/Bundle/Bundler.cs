﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.NET.HostModel.AppHost;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace Microsoft.NET.HostModel.Bundle
{
    /// <summary>
    /// Bundler: Functionality to embed the managed app and its dependencies
    /// into the host native binary.
    /// </summary>
    public class Bundler
    {
        readonly string HostName;
        readonly string OutputDir;
        readonly string DepsJson;
        readonly string RuntimeConfigJson;
        readonly string RuntimeConfigDevJson;

        readonly Trace Tracer;
        public readonly Manifest BundleManifest;
        readonly TargetInfo Target;
        readonly BundleOptions Options;

        // Assemblies are 16 bytes aligned, so that their sections can be memory-mapped cache aligned.
        public const int AssemblyAlignment = 16;

        public Bundler(string hostName,
                       string outputDir,
                       BundleOptions options = BundleOptions.None,
                       OSPlatform? targetOS = null,
                       Version targetFrameworkVersion = null,
                       bool diagnosticOutput = false)
        {
            Tracer = new Trace(diagnosticOutput);

            HostName = hostName;
            OutputDir = Path.GetFullPath(string.IsNullOrEmpty(outputDir) ? Environment.CurrentDirectory : outputDir);

            string baseName = Path.GetFileNameWithoutExtension(HostName);
            DepsJson = baseName + ".deps.json";
            RuntimeConfigJson = baseName + ".runtimeconfig.json";
            RuntimeConfigDevJson = baseName + ".runtimeconfig.dev.json";

            Target = new TargetInfo(targetOS, targetFrameworkVersion);
            BundleManifest = new Manifest(Target.BundleVersion, netcoreapp3CompatMode: options.HasFlag(BundleOptions.BundleAllContent));
            Options = Target.DefaultOptions | options;
        }

        /// <summary>
        /// Embed 'file' into 'bundle'
        /// </summary>
        /// <returns>Returns the offset of the start 'file' within 'bundle'</returns>

        long AddToBundle(Stream bundle, Stream file, FileType type)
        {
            if (type == FileType.Assembly)
            {
                long misalignment = (bundle.Position % AssemblyAlignment);

                if (misalignment != 0)
                {
                    long padding = AssemblyAlignment - misalignment;
                    bundle.Position += padding;
                }
            }

            file.Position = 0;
            long startOffset = bundle.Position;
            file.CopyTo(bundle);

            return startOffset;
        }

        bool IsHost(string fileRelativePath)
        {
            return fileRelativePath.Equals(HostName);
        }

        bool ShouldIgnore(string fileRelativePath)
        {
            return fileRelativePath.Equals(RuntimeConfigDevJson);
        }

        bool ShouldExclude(FileType type)
        {
            switch (type)
            {
                case FileType.Assembly:
                case FileType.DepsJson:
                case FileType.RuntimeConfigJson:
                    return false;

                case FileType.NativeBinary:
                    return !Options.HasFlag(BundleOptions.BundleNativeBinaries);

                case FileType.Symbols:
                    return !Options.HasFlag(BundleOptions.BundleSymbolFiles);

                case FileType.Unknown:
                    return !Options.HasFlag(BundleOptions.BundleOtherFiles);

                default:
                    Debug.Assert(false);
                    return false;
            }
        }

        bool IsAssembly(string path, out bool isPE)
        {
            isPE = false;

            using (FileStream file = File.OpenRead(path))
            {
                try
                {
                    PEReader peReader = new PEReader(file);
                    CorHeader corHeader = peReader.PEHeaders.CorHeader;

                    isPE = true; // If peReader.PEHeaders doesn't throw, it is a valid PEImage
                    return corHeader != null;
                }
                catch (BadImageFormatException)
                {
                }
            }

            return false;
        }

        FileType InferType(FileSpec fileSpec)
        {
            if (fileSpec.BundleRelativePath.Equals(DepsJson))
            {
                return FileType.DepsJson;
            }

            if (fileSpec.BundleRelativePath.Equals(RuntimeConfigJson))
            {
                return FileType.RuntimeConfigJson;
            }

            if (Path.GetExtension(fileSpec.BundleRelativePath).ToLowerInvariant().Equals(".pdb"))
            {
                return FileType.Symbols;
            }

            bool isPE;
            if (IsAssembly(fileSpec.SourcePath, out isPE))
            {
                return FileType.Assembly;
            }

            bool isNativeBinary = Target.IsWindows ? isPE : Target.IsNativeBinary(fileSpec.SourcePath);

            if (isNativeBinary)
            {
                return FileType.NativeBinary;
            }

            return FileType.Unknown;
        }

        /// <summary>
        /// Generate a bundle, given the specification of embedded files
        /// </summary>
        /// <param name="fileSpecs">
        /// An enumeration FileSpecs for the files to be embedded.
        /// 
        /// Files in fileSpecs that are not bundled within the single file bundle,
        /// and should be published as separate files are marked as "IsExcluded" by this method.
        /// This doesn't include unbundled files that should be dropped, and not publised as output.
        /// </param>
        /// <returns>
        /// The full path the the generated bundle file
        /// </returns>
        /// <exceptions>
        /// ArgumentException if input is invalid
        /// IOExceptions and ArgumentExceptions from callees flow to the caller.
        /// </exceptions>
        public string GenerateBundle(IReadOnlyList<FileSpec> fileSpecs)
        {
            Tracer.Log($"Bundler version: {Manifest.CurrentVersion}");
            Tracer.Log($"Bundler Header: {BundleManifest.DesiredVersion}");
            Tracer.Log($"Target Runtime: {Target}");
            Tracer.Log($"Bundler Options: {Options}");

            if (fileSpecs.Any(x => !x.IsValid()))
            {
                throw new ArgumentException("Invalid input specification: Found entry with empty source-path or bundle-relative-path.");
            }

            string hostSource;
            try
            {
                hostSource = fileSpecs.Where(x => x.BundleRelativePath.Equals(HostName)).Single().SourcePath;
            }
            catch (InvalidOperationException)
            {
                throw new ArgumentException("Invalid input specification: Must specify the host binary");
            }

            if (fileSpecs.GroupBy(file => file.BundleRelativePath).Where(g => g.Count() > 1).Any())
            {
                throw new ArgumentException("Invalid input specification: Found multiple entries with the same BundleRelativePath");
            }

            string bundlePath = Path.Combine(OutputDir, HostName);
            if (File.Exists(bundlePath))
            {
                Tracer.Log($"Ovewriting existing File {bundlePath}");
            }

            BinaryUtils.CopyFile(hostSource, bundlePath);

            long headerOffset = 0;
            using (BinaryWriter writer = new BinaryWriter(File.OpenWrite(bundlePath)))
            {
                Stream bundle = writer.BaseStream;
                bundle.Position = bundle.Length;

                foreach (var fileSpec in fileSpecs)
                {
                    if (IsHost(fileSpec.BundleRelativePath))
                    {
                        continue;
                    }

                    if (ShouldIgnore(fileSpec.BundleRelativePath))
                    {
                        Tracer.Log($"Ignore: {fileSpec.BundleRelativePath}");
                        continue;
                    }

                    FileType type = InferType(fileSpec);

                    if (ShouldExclude(type))
                    {
                        Tracer.Log($"Exclude [{type}]: {fileSpec.BundleRelativePath}");
                        fileSpec.Excluded = true;
                        continue;
                    }

                    using (FileStream file = File.OpenRead(fileSpec.SourcePath))
                    {
                        FileType targetType = Target.TargetSpecificFileType(type);
                        long startOffset = AddToBundle(bundle, file, targetType);
                        FileEntry entry = BundleManifest.AddEntry(targetType, fileSpec.BundleRelativePath, startOffset, file.Length);
                        Tracer.Log($"Embed: {entry}");
                    }
                }

                // Write the bundle manifest
                headerOffset = BundleManifest.Write(writer);
                Tracer.Log($"Header Offset={headerOffset}");
                Tracer.Log($"Meta-data Size={writer.BaseStream.Position - headerOffset}");
                Tracer.Log($"Bundle: Path={bundlePath}, Size={bundle.Length}");
            }

            HostWriter.SetAsBundle(bundlePath, headerOffset);

            return bundlePath;
        }
    }
}

