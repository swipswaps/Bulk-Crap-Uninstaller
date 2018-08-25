/*
    Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/)
    Apache License Version 2.0
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Klocman.Extensions;
using Klocman.Tools;

namespace UninstallTools.Factory.InfoAdders
{
    public class AppExecutablesSearcher : IMissingInfoAdder
    {
        internal static readonly string[] BinaryDirectoryNames =
        {
            "bin", "bin32", "bin64", "program", "client", "app", "application", "win32", "win64" //"system"
        };

        public string[] CanProduceValueNames { get; } = {
            nameof(ApplicationUninstallerEntry.SortedExecutables)
        };

        public InfoAdderPriority Priority { get; } = InfoAdderPriority.RunFirst;

        public string[] RequiredValueNames { get; } = {
            nameof(ApplicationUninstallerEntry.InstallLocation)
        };

        public bool RequiresAllValues { get; } = true;
        public bool AlwaysRun { get; } = false;

        public void AddMissingInformation(ApplicationUninstallerEntry target)
        {
            /*var trimmedDispName = target.DisplayNameTrimmed;
            if (string.IsNullOrEmpty(trimmedDispName) || trimmedDispName.Length < 5)
            {
                trimmedDispName = target.DisplayName;
                if (string.IsNullOrEmpty(trimmedDispName))
                    // Impossible to search for the executable without knowing the app name
                    return;
            }*/

            if (!UninstallToolsGlobalConfig.IO.DirectoryExists(target.InstallLocation))
                return;

            var trimmedDispName = target.DisplayNameTrimmed;

            try
            {
                var results = ScanDirectory(target.InstallLocation);

                target.SortedExecutables = SortListExecutables(results.ExecutableFiles, trimmedDispName).ToArray();
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        internal static ScanDirectoryResult ScanDirectory(string directory)
        {
            IEnumerable<string> GetExeFiles(string dir)
            {
                return UninstallToolsGlobalConfig.IO.GetFiles(dir).Where(x => x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            }

            var results = GetExeFiles(directory).ToList();
            var otherSubdirs = new List<string>();
            var binSubdirs = new List<string>();
            foreach (var subdir in UninstallToolsGlobalConfig.IO.GetDirectories(directory))
            {
                try
                {
                    var subName = Path.GetDirectoryName(subdir) ?? string.Empty;
                    if (subName.StartsWithAny(BinaryDirectoryNames, StringComparison.OrdinalIgnoreCase))
                    {
                        binSubdirs.Add(subdir);
                        results.AddRange(GetExeFiles(subdir));
                    }
                    else
                    {
                        // Directories with very short names likely contain program files
                        if (subName.Length > 3 &&
                            // This skips ISO language codes, much faster than a more specific compare
                            (subName.Length != 5 || !subName[2].Equals('-')))
                            otherSubdirs.Add(subdir);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return new ScanDirectoryResult(results, binSubdirs, otherSubdirs);
        }

        internal static IEnumerable<string> SortListExecutables(IEnumerable<string> targets, string targetString)
        {
            return from target in targets
                   let name = Path.GetFileName(target)
                   where name != null
                   orderby Sift4.SimplestDistance(name, targetString, 3)
                   select target;
        }

        internal sealed class ScanDirectoryResult
        {
            public ScanDirectoryResult(ICollection<string> executableFiles,
                ICollection<string> binSubdirs, ICollection<string> otherSubdirs)
            {
                OtherSubdirs = otherSubdirs;
                ExecutableFiles = executableFiles;
                BinSubdirs = binSubdirs;
            }

            public ICollection<string> BinSubdirs { get; }
            public ICollection<string> ExecutableFiles { get; }
            public ICollection<string> OtherSubdirs { get; }
        }
    }
}