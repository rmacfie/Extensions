// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Framework.Expiration.Interfaces;

namespace Microsoft.AspNet.FileProviders
{
    /// <summary>
    /// Looks up files using the on-disk file system
    /// </summary>
    public class PhysicalFileProvider : IFileProvider
    {
        // These are restricted file names on Windows, regardless of extension.
        private static readonly Dictionary<string, string> RestrictedFileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "con", string.Empty },
            { "prn", string.Empty },
            { "aux", string.Empty },
            { "nul", string.Empty },
            { "com1", string.Empty },
            { "com2", string.Empty },
            { "com3", string.Empty },
            { "com4", string.Empty },
            { "com5", string.Empty },
            { "com6", string.Empty },
            { "com7", string.Empty },
            { "com8", string.Empty },
            { "com9", string.Empty },
            { "lpt1", string.Empty },
            { "lpt2", string.Empty },
            { "lpt3", string.Empty },
            { "lpt4", string.Empty },
            { "lpt5", string.Empty },
            { "lpt6", string.Empty },
            { "lpt7", string.Empty },
            { "lpt8", string.Empty },
            { "lpt9", string.Empty },
            { "clock$", string.Empty },
        };

        private readonly PhysicalFilesWatcher _filesWatcher;

        /// <summary>
        /// Creates a new instance of a PhysicalFileProvider at the given root directory.
        /// </summary>
        /// <param name="root">The root directory. This should be an absolute path.</param>
        public PhysicalFileProvider(string root)
        {
            if (!Path.IsPathRooted(root))
            {
                throw new ArgumentException("The path must be absolute.", nameof(root));
            }
            var fullRoot = Path.GetFullPath(root);
            // When we do matches in GetFullPath, we want to only match full directory names.
            Root = EnsureTrailingSlash(fullRoot);
            if (!Directory.Exists(Root))
            {
                throw new DirectoryNotFoundException(Root);
            }

            // Monitor only the application's root folder.
            _filesWatcher = new PhysicalFilesWatcher(Root);
        }

        /// <summary>
        /// The root directory for this instance.
        /// </summary>
        public string Root { get; private set; }

        private string GetFullPath(string path)
        {
            var fullPath = Path.GetFullPath(Path.Combine(Root, path));
            if (!IsUnderneathRoot(fullPath))
            {
                return null;
            }
            return fullPath;
        }

        private bool IsUnderneathRoot(string fullPath)
        {
            return fullPath.StartsWith(Root, StringComparison.OrdinalIgnoreCase);
        }

        private static string EnsureTrailingSlash(string path)
        {
            if (!string.IsNullOrEmpty(path) &&
                path[path.Length - 1] != Path.DirectorySeparatorChar)
            {
                return path + Path.DirectorySeparatorChar;
            }

            return path;
        }

        /// <summary>
        /// Locate a file at the given path by directly mapping path segments to physical directories.
        /// </summary>
        /// <param name="subpath">A path under the root directory</param>
        /// <returns>The file information. Caller must check Exists property. </returns>
        public IFileInfo GetFileInfo(string subpath)
        {
            if (string.IsNullOrEmpty(subpath))
            {
                return new NotFoundFileInfo(subpath);
            }

            // Relative paths starting with a leading slash okay
            if (subpath.StartsWith("/", StringComparison.Ordinal))
            {
                subpath = subpath.Substring(1);
            }

            // Absolute paths not permitted.
            if (Path.IsPathRooted(subpath))
            {
                return new NotFoundFileInfo(subpath);
            }

            var fullPath = GetFullPath(subpath);
            if (fullPath == null || IsRestricted(subpath))
            {
                return new NotFoundFileInfo(subpath);
            }

            var fileInfo = new FileInfo(fullPath);
            if (FileSystemInfoHelper.IsHiddenFile(fileInfo))
            {
                return new NotFoundFileInfo(subpath);
            }

            if (fileInfo.Exists)
            {
                return new PhysicalFileInfo(_filesWatcher, fileInfo);
            }

            return new NotFoundFileInfo(subpath);
        }

        /// <summary>
        /// Enumerate a directory at the given path, if any.
        /// </summary>
        /// <param name="subpath">A path under the root directory</param>
        /// <returns>Contents of the directory. Caller must check Exists property.</returns>
        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            try
            {
                if (subpath == null)
                {
                    return new NotFoundDirectoryContents();
                }

                // Relative paths starting with a leading slash okay
                if (subpath.StartsWith("/", StringComparison.Ordinal))
                {
                    subpath = subpath.Substring(1);
                }

                // Absolute paths not permitted.
                if (Path.IsPathRooted(subpath))
                {
                    return new NotFoundDirectoryContents();
                }

                var fullPath = GetFullPath(subpath);
                if (fullPath != null)
                {
                    var directoryInfo = new DirectoryInfo(fullPath);
                    if (!directoryInfo.Exists)
                    {
                        return new NotFoundDirectoryContents();
                    }

                    var physicalInfos = directoryInfo
                        .EnumerateFileSystemInfos()
                        .Where(info => !FileSystemInfoHelper.IsHiddenFile(info));
                    var virtualInfos = new List<IFileInfo>();
                    foreach (var fileSystemInfo in physicalInfos)
                    {
                        var fileInfo = fileSystemInfo as FileInfo;
                        if (fileInfo != null)
                        {
                            virtualInfos.Add(new PhysicalFileInfo(_filesWatcher, fileInfo));
                        }
                        else
                        {
                            virtualInfos.Add(new PhysicalDirectoryInfo((DirectoryInfo)fileSystemInfo));
                        }
                    }

                    return new EnumerableDirectoryContents(virtualInfos);
                }
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
            return new NotFoundDirectoryContents();
        }

        private bool IsRestricted(string name)
        {
            string fileName = Path.GetFileNameWithoutExtension(name);
            return RestrictedFileNames.ContainsKey(fileName);
        }

        public IExpirationTrigger Watch(string filter)
        {
            if (filter == null)
            {
                return NoopTrigger.Singleton;
            }

            // Relative paths starting with a leading slash okay
            if (filter.StartsWith("/", StringComparison.Ordinal))
            {
                filter = filter.Substring(1);
            }

            // Absolute paths not permitted.
            if (Path.IsPathRooted(filter))
            {
                return NoopTrigger.Singleton;
            }

            return _filesWatcher.CreateFileChangeTrigger(filter);
        }

        private class PhysicalFileInfo : IFileInfo
        {
            private readonly FileInfo _info;

            private readonly PhysicalFilesWatcher _filesWatcher;

            public PhysicalFileInfo(PhysicalFilesWatcher filesWatcher, FileInfo info)
            {
                _info = info;
                _filesWatcher = filesWatcher;
            }

            public bool Exists
            {
                get { return _info.Exists; }
            }

            public long Length
            {
                get { return _info.Length; }
            }

            public string PhysicalPath
            {
                get { return _info.FullName; }
            }

            public string Name
            {
                get { return _info.Name; }
            }

            public DateTimeOffset LastModified
            {
                get
                {
                    return _info.LastWriteTimeUtc;
                }
            }

            public bool IsDirectory
            {
                get { return false; }
            }

            public bool IsReadOnly
            {
                get
                {
                    return _info.IsReadOnly;
                }
            }

            public Stream CreateReadStream()
            {
                // Note: Buffer size must be greater than zero, even if the file size is zero.
                return new FileStream(PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 64,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
            }

            public void WriteContent(byte[] content)
            {
                File.WriteAllBytes(PhysicalPath, content);
                _info.Refresh();
            }

            public void Delete()
            {
                File.Delete(PhysicalPath);
                _info.Refresh();
            }
        }

        private class PhysicalDirectoryInfo : IFileInfo
        {
            private readonly DirectoryInfo _info;

            public PhysicalDirectoryInfo(DirectoryInfo info)
            {
                _info = info;
            }

            public bool Exists
            {
                get { return _info.Exists; }
            }

            public long Length
            {
                get { return -1; }
            }

            public string PhysicalPath
            {
                get { return _info.FullName; }
            }

            public string Name
            {
                get { return _info.Name; }
            }

            public DateTimeOffset LastModified
            {
                get
                {
                    return _info.LastWriteTimeUtc;
                }
            }

            public bool IsDirectory
            {
                get { return true; }
            }

            public bool IsReadOnly
            {
                get { return true; }
            }

            public Stream CreateReadStream()
            {
                throw new InvalidOperationException("Cannot create a stream for a directory.");
            }

            public void WriteContent(byte[] content)
            {
                throw new InvalidOperationException("Cannot write content into a directory.");
            }

            public void Delete()
            {
                Directory.Delete(PhysicalPath, recursive: true);
            }
        }
    }
}