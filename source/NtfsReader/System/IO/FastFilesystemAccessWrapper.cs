using System.Collections.Generic;
using System.IO.Filesystem.Ntfs;
using System.Linq;

namespace System.IO
{
    public class FastFilesystemAccessWrapper
    {
        ~FastFilesystemAccessWrapper()
        {
            ResetState();
        }

        public FastFilesystemAccessWrapper()
        {
            ReloadFilesystemInfo();
        }

        private readonly Dictionary<string, NtfsReader> _readers = new Dictionary<string, NtfsReader>();
        private readonly List<INode> _nodes = new List<INode>();

        public void ReloadFilesystemInfo()
        {
            ResetState();

            foreach (var driveInfo in DriveInfo.GetDrives())
            {
                if (!driveInfo.IsReady) continue;

                switch (driveInfo.DriveType)
                {
                    case DriveType.Ram:
                    case DriveType.Removable:
                    case DriveType.Unknown:
                    case DriveType.Fixed:
                        if (!string.IsNullOrEmpty(driveInfo.Name))
                        {
                            if (string.Equals(driveInfo.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                            {
                                NtfsReader ntfsReader;
                                try
                                {
                                    ntfsReader = new NtfsReader(driveInfo, RetrieveMode.StandardInformations);
                                }
                                catch (SystemException ex)
                                {
                                    Console.WriteLine(ex);
                                    continue;
                                }
                                _readers.Add(driveInfo.Name.TrimEnd(':', '\\', ' ').ToLower(), ntfsReader);
                                _nodes.AddRange(ntfsReader.GetNodes(driveInfo.Name));
                            }
                        }
                        break;

                    case DriveType.NoRootDirectory:
                        break;
                    case DriveType.Network:
                        break;
                    case DriveType.CDRom:
                        break;
                }
            }
        }

        private void ResetState()
        {
            _nodes.Clear();
            foreach (var ntfsReader in _readers.Values)
                ntfsReader.Dispose();
            _readers.Clear();
        }

        public bool FileExists(string path)
        {
            if (!IsReady(GetPathRoot(path))) return File.Exists(path);

            var node = GetFilesystemNode(path, false);
            return node != null;
        }

        public bool DirectoryExists(string path)
        {
            if (!IsReady(GetPathRoot(path))) return Directory.Exists(path);

            var node = GetFilesystemNode(path, true);
            return node != null;
        }

        public IEnumerable<string> GetFiles(string path, SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            if (!IsReady(GetPathRoot(path)))
                return Directory.GetFiles(path, "*", searchOption);

            return GetFilesystemEntries(path, searchOption).Where(x => !IsDirectory(x)).Select(x => x.FullName);
        }

        public IEnumerable<string> GetDirectories(string path, SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            if (!IsReady(GetPathRoot(path)))
                return Directory.GetDirectories(path, "*", searchOption);

            return GetFilesystemEntries(path, searchOption).Where(IsDirectory).Select(x => x.FullName);
        }

        private IEnumerable<INode> GetFilesystemEntries(string path, SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            var dir = GetFilesystemNode(path, true);
            if (dir == null) return new INode[] { };

            switch (searchOption)
            {
                case SearchOption.TopDirectoryOnly:
                    return _nodes.Where(x => x.ParentNodeIndex == dir.NodeIndex);
                case SearchOption.AllDirectories:
                    return _nodes.Where(x => x.FullName.StartsWith(path, StringComparison.OrdinalIgnoreCase) && dir.NodeIndex != x.NodeIndex);
                default:
                    throw new ArgumentOutOfRangeException(nameof(searchOption), searchOption, null);
            }
        }

        private INode GetFilesystemNode(string path, bool directory)
        {
            return _nodes.FirstOrDefault(x => x.FullName.Equals(path, StringComparison.OrdinalIgnoreCase) && IsDirectory(x) == directory);
        }

        private static bool IsDirectory(INode x)
        {
            return (x.Attributes & Attributes.Directory) != 0;
        }

        private static string GetPathRoot(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            var i = path.IndexOf(':');
            if (i <= 0)
                return null;
            var r = path.Substring(0, i).TrimStart('"', ' ').ToLower();
            return r.Length == 0 ? null : r;
        }

        private bool IsReady(string root)
        {
            return root != null && _nodes.Count > 0 && _readers.ContainsKey(root);
        }

        public bool DirectoryHasSystemAttribute(string path)
        {
            if (!IsReady(GetPathRoot(path)))
            {
                var dir = new DirectoryInfo(path);
                return (dir.Attributes & FileAttributes.System) == FileAttributes.System;
            }

            var node = GetFilesystemNode(path, true);
            return node != null && (node.Attributes & Attributes.System) == Attributes.System;
        }

        public DateTime GetDirectoryCreationTime(string directory)
        {
            if (!IsReady(GetPathRoot(directory)))
                return Directory.GetCreationTime(directory);

            var n = GetFilesystemNode(directory, true);
            return n.CreationTime;
        }
        public DateTime GetFileCreationTime(string file)
        {
            if (!IsReady(GetPathRoot(file)))
                return File.GetCreationTime(file);

            var n = GetFilesystemNode(file, false);
            return n.CreationTime;
        }
    }
}
