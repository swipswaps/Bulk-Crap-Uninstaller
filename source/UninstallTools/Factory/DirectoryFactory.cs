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
using UninstallTools.Factory.InfoAdders;
using UninstallTools.Properties;

namespace UninstallTools.Factory
{
    using KVP = KeyValuePair<string, bool?>;

    public class DirectoryFactory : IUninstallerFactory
    {
        private readonly IEnumerable<ApplicationUninstallerEntry> _existingUninstallerEntries;

        public DirectoryFactory(IEnumerable<ApplicationUninstallerEntry> existing)
        {
            _existingUninstallerEntries = existing;
        }

        public IEnumerable<ApplicationUninstallerEntry> GetUninstallerEntries(ListGenerationProgress.ListGenerationCallback progressCallback)
        {
            progressCallback(new ListGenerationProgress(0, -1, Localisation.Progress_DriveScan_Gathering));

            var existingUninstallers = _existingUninstallerEntries.ToList();

            var pfDirs = UninstallToolsGlobalConfig.GetProgramFilesDirectories(true).ToList();
            var dirsToSkip = GetDirectoriesToSkip(existingUninstallers, pfDirs).ToList();

            var itemsToScan = GetDirectoriesToScan(existingUninstallers, pfDirs, dirsToSkip).ToList();

            var progress = 0;
            var results = new List<ApplicationUninstallerEntry>();
            foreach (var directory in itemsToScan)
            {
                progressCallback(new ListGenerationProgress(progress++, itemsToScan.Count, directory.Key));

                if (UninstallToolsGlobalConfig.IsSystemDirectory(directory.Key) ||
                    Path.GetFileName(directory.Key)?.StartsWith("Windows", StringComparison.InvariantCultureIgnoreCase) != false)
                    continue;

                var detectedEntries = TryCreateFromDirectory(directory.Key, directory.Value, dirsToSkip).ToList();

                results = ApplicationUninstallerFactory.MergeResults(results, detectedEntries, null);
            }

            return results;
        }

        public static IEnumerable<ApplicationUninstallerEntry> TryGetApplicationsFromDirectories(
            ICollection<string> directoriesToScan, IEnumerable<ApplicationUninstallerEntry> existingUninstallers)
        {
            var pfDirs = UninstallToolsGlobalConfig.GetProgramFilesDirectories(true).ToList();
            var dirsToSkip = GetDirectoriesToSkip(existingUninstallers, pfDirs).ToList();

            var results = new List<ApplicationUninstallerEntry>();
            foreach (var directory in directoriesToScan)
            {
                if (UninstallToolsGlobalConfig.IsSystemDirectory(directory) ||
                    Path.GetFileName(directory)?.StartsWith("Windows", StringComparison.InvariantCultureIgnoreCase) != false)
                    continue;

                var detectedEntries = TryCreateFromDirectory(directory, null, dirsToSkip);

                results.AddRange(detectedEntries);
            }
            return results;
        }

        /// <summary>
        /// Get directories to scan for applications
        /// </summary>
        private static IEnumerable<KVP> GetDirectoriesToScan(IEnumerable<ApplicationUninstallerEntry> existingUninstallers,
            IEnumerable<KVP> pfDirs, IEnumerable<string> dirsToSkip)
        {
            var pfDirectories = pfDirs.ToList();

            if (UninstallToolsGlobalConfig.AutoDetectCustomProgramFiles)
            {
                var extraPfDirectories = FindExtraPfDirectories(existingUninstallers)
                  .Where(extraDir => !extraDir.Key.Contains(@"\Common Files", StringComparison.InvariantCultureIgnoreCase))
                  .Where(extraDir => pfDirectories.All(pfDir => !PathTools.PathsEqual(pfDir.Key, extraDir.Key)));

                pfDirectories.AddRange(extraPfDirectories);
            }

            var directoriesToSkip = dirsToSkip.ToList();

            // Get sub directories which could contain user programs
            var directoriesToCheck = pfDirectories.SelectMany(x =>
            {
                try
                {
                    return UninstallToolsGlobalConfig.IO.GetDirectories(x.Key).Select(y => new KVP(y, x.Value));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                return Enumerable.Empty<KVP>();
            });

            // Get directories that can be relatively safely checked
            return directoriesToCheck.Where(check => !directoriesToSkip.Any(skip =>
                check.Key.Contains(skip, StringComparison.InvariantCultureIgnoreCase)))
                .Distinct((pair, otherPair) => PathTools.PathsEqual(pair.Key, otherPair.Key));
        }

        /// <summary>
        /// Get directories which are already used and should be skipped
        /// </summary>
        private static IEnumerable<string> GetDirectoriesToSkip(IEnumerable<ApplicationUninstallerEntry> existingUninstallers,
            IEnumerable<KVP> pfDirectories)
        {
            var dirs = new List<string>();
            foreach (var x in existingUninstallers)
            {
                dirs.Add(x.InstallLocation);
                dirs.Add(x.UninstallerLocation);

                if (string.IsNullOrEmpty(x.DisplayIcon)) continue;
                try
                {
                    var iconFilename = x.DisplayIcon.Contains('.')
                        ? ProcessTools.SeparateArgsFromCommand(x.DisplayIcon).FileName
                        : x.DisplayIcon;

                    dirs.Add(PathTools.GetDirectory(iconFilename));
                }
                catch
                {
                    // Ignore invalid DisplayIcon paths
                }
            }

            return dirs.Where(x => !string.IsNullOrEmpty(x)).Distinct()
                .Where(x => !pfDirectories.Any(pfd => pfd.Key.Contains(x, StringComparison.InvariantCultureIgnoreCase)));
        }

        private static IEnumerable<KVP> FindExtraPfDirectories(IEnumerable<ApplicationUninstallerEntry> existingUninstallers)
        {
            var extraSearchLocations = existingUninstallers
                .Select(x => x.InstallLocation)
                .Where(x => !string.IsNullOrEmpty(x))
                .Select(s =>
                {
                    try
                    {
                        return Path.GetDirectoryName(s);
                    }
                    catch (ArgumentException)
                    {
                        return null;
                    }
                }).Where(x => x != null)
                .GroupBy(x => x.ToLowerInvariant())
                // Select only groups with 2 or more hits
                .Where(g => g.Take(2).Count() == 2)
                .Select(g => g.Key);

            return extraSearchLocations.Select(x =>
            {
                try
                {
                    var directoryPath = PathTools.PathToNormalCase(x).TrimEnd('\\');
                    return UninstallToolsGlobalConfig.IO.DirectoryExists(directoryPath) ? directoryPath : null;
                }
                catch
                {
                    return null;
                }
            }).Where(x => x != null)
            .Select(x => new KVP(x, null));
        }
        
        private static void CreateFromDirectoryHelper(ICollection<ApplicationUninstallerEntry> results,
            string directory, int level, ICollection<string> dirsToSkip)
        {
            // Level 0 is for the pf folder itself. First subfolder is level 1.
            if (level > 2 || dirsToSkip.Any(x => directory.Contains(x, StringComparison.InvariantCultureIgnoreCase)))
                return;

            // Get contents of this installDir
            AppExecutablesSearcher.ScanDirectoryResult result;

            try
            {
                result = AppExecutablesSearcher.ScanDirectory(directory);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }

            // Check if it is potentially dangerous to process this installDir.
            if (result.ExecutableFiles.Count > 40)
                return;

            var anyFiles = result.ExecutableFiles.Any();
            if (!anyFiles && !result.BinSubdirs.Any())
            {
                foreach (var dir in result.OtherSubdirs)
                    CreateFromDirectoryHelper(results, dir, level + 1, dirsToSkip);
            }
            else if (anyFiles)
            {
                var entry = new ApplicationUninstallerEntry();

                var dirName = Path.GetFileName(directory);
                var parent = Path.GetDirectoryName(directory);
                var parentName = Path.GetFileName(parent);

                // Parse directories into useful information
                if (level > 0 && dirName.StartsWithAny(AppExecutablesSearcher.BinaryDirectoryNames, StringComparison.OrdinalIgnoreCase))
                {
                    entry.InstallLocation = parent;
                    entry.RawDisplayName = parentName;
                }
                else
                {
                    entry.InstallLocation = directory;
                    entry.RawDisplayName = dirName;

                    if (level > 0)
                        entry.Publisher = parentName;
                }

                var sorted = AppExecutablesSearcher.SortListExecutables(result.ExecutableFiles, entry.DisplayNameTrimmed).ToArray();
                entry.SortedExecutables = sorted;

                entry.InstallDate = UninstallToolsGlobalConfig.IO.GetDirectoryCreationTime(directory);
                //entry.IconBitmap = TryExtractAssociatedIcon(compareBestMatchFile.FullName);

                // Extract info from file metadata and overwrite old values
                var compareBestMatchFile = sorted.First();
                ExecutableAttributeExtractor.FillInformationFromFileAttribs(entry, compareBestMatchFile, false);

                results.Add(entry);
            }
        }

        /// <summary>
        /// Try to get the main executable from the filtered folders. If no executables are present check subfolders.
        /// </summary>
        public static IEnumerable<ApplicationUninstallerEntry> TryCreateFromDirectory(string directory, bool? is64Bit,
            ICollection<string> dirsToSkip)
        {
            if (directory == null)
                throw new ArgumentNullException(nameof(directory));

            var results = new List<ApplicationUninstallerEntry>();

            CreateFromDirectoryHelper(results, directory, 0, dirsToSkip);

            foreach (var tempEntry in results)
            {
                if (is64Bit.HasValue && tempEntry.Is64Bit == MachineType.Unknown)
                    tempEntry.Is64Bit = is64Bit.Value ? MachineType.X64 : MachineType.X86;

                tempEntry.IsRegistered = false;
                tempEntry.IsOrphaned = true;

                tempEntry.UninstallerKind = tempEntry.UninstallPossible
                    ? UninstallerTypeAdder.GetUninstallerType(tempEntry.UninstallString)
                    : UninstallerType.SimpleDelete;
            }

            return results;
        }

        public static IEnumerable<ApplicationUninstallerEntry> TryCreateFromDirectory(
            string directoryToScan, IEnumerable<ApplicationUninstallerEntry> existingUninstallers)
        {
            var pfDirs = UninstallToolsGlobalConfig.GetProgramFilesDirectories(true).ToList();
            var dirsToSkip = GetDirectoriesToSkip(existingUninstallers, pfDirs).ToList();

            if (UninstallToolsGlobalConfig.IsSystemDirectory(directoryToScan) ||
                Path.GetFileName(directoryToScan)?.StartsWith("Windows", StringComparison.InvariantCultureIgnoreCase) != false)
                return Enumerable.Empty<ApplicationUninstallerEntry>();

            return TryCreateFromDirectory(directoryToScan, null, dirsToSkip);
            }
    }
}