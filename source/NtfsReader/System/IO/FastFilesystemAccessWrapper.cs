using System.Collections.Generic;
using System.IO.Filesystem.Ntfs;
using System.Linq;
using Klocman.Extensions;
using Klocman.Tools;

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

        private sealed class NodeEntry
        {
            public NodeEntry(INode node, Dictionary<string, NodeEntry> subNodes = null)
            {
                Node = node;
                SubNodes = subNodes ?? new Dictionary<string, NodeEntry>();
            }

            public INode Node { get; }
            public Dictionary<string, NodeEntry> SubNodes { get; }
        }

        private readonly Dictionary<string, NtfsReader> _readers = new Dictionary<string, NtfsReader>();
        private readonly Dictionary<string, NodeEntry> _nodes = new Dictionary<string, NodeEntry>();

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

                                // nodes are in order based on their indexes, so it's easy to find parents
                                var nodes = ntfsReader.GetNodes(driveInfo.Name);
                                // need to add \ to the end for the sorting to work correctly
                                nodes.Sort((node, node2) => string.Compare(node.FullName + '\\', node2.FullName + '\\', StringComparison.Ordinal));
                                
                                var rootNodeName = driveInfo.Name + '.';
                                var root = nodes.First(x => string.Equals(x.FullName, rootNodeName, StringComparison.OrdinalIgnoreCase));

                                var path = new Stack<NodeEntry>();
                                foreach (var node in nodes)
                                {
                                    // todo put new dirs onto stack, add subdirs/files to it and add those to the stack, 
                                    // pop if next file is not in this path (startswith)
                                    // when making nodes make keys ToLowerInvariant

                                    //_nodes.Add(root.FullName.ToLowerInvariant(), new NodeEntry(root, GetSubnodes(root)));
                                }
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
                    return dir.SubNodes.Values.Select(x => x.Node);
                case SearchOption.AllDirectories:
                    return dir.SubNodes.SelectManyResursively(pair => pair.Value.SubNodes).Select(x => x.Value.Node);
                default:
                    throw new ArgumentOutOfRangeException(nameof(searchOption), searchOption, null);
            }
        }

        private NodeEntry GetFilesystemNode(string path, bool directory)
        {
            if (path == null) return null;
            
            var pathParts = path.ToLowerInvariant().Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

            var currentNodes = _nodes;
            for (var i = 0; i < pathParts.Length; i++)
            {
                var part = pathParts[i];
                if (i == 0) part += '.';

                if (!currentNodes.TryGetValue(part, out var node)) return null;

                if (i == pathParts.Length - 1) return IsDirectory(node.Node) == directory ? node : null;

                currentNodes = node.SubNodes;
            }

            //todo if (path != null && _nodes.TryGetValue(path.ToLowerInvariant(), out var n) && IsDirectory(n) == directory)
            //    return n;

            return null;
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
            return node != null && (node.Node.Attributes & Attributes.System) == Attributes.System;
        }

        public DateTime GetDirectoryCreationTime(string directory)
        {
            if (!IsReady(GetPathRoot(directory)))
                return Directory.GetCreationTime(directory);

            var n = GetFilesystemNode(directory, true);
            return n.Node.CreationTime;
        }
        public DateTime GetFileCreationTime(string file)
        {
            if (!IsReady(GetPathRoot(file)))
                return File.GetCreationTime(file);

            var n = GetFilesystemNode(file, false);
            return n.Node.CreationTime;
        }
    }
}
