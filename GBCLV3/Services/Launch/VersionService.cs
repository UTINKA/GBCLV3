﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GBCLV3.Models.Download;
using GBCLV3.Models.Launch;
using GBCLV3.Services.Download;
using GBCLV3.Utils;
using StyletIoC;
using Version = GBCLV3.Models.Launch.Version;

namespace GBCLV3.Services.Launch
{
    public class VersionService
    {
        #region Constructor

        [Inject]
        public VersionService(GamePathService gamePathService, DownloadUrlService urlService)
        {
            _gamePathService = gamePathService;
            _urlService = urlService;
            _versions = new Dictionary<string, Version>(8);

            _client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        }

        #endregion

        #region Events

        public event Action<bool> Loaded;

        public event Action<Version> Deleted;

        public event Action<Version> Created;

        #endregion

        #region Private Fields

        private readonly Dictionary<string, Version> _versions;

        private readonly HttpClient _client;

        // IoC
        private readonly GamePathService _gamePathService;
        private readonly DownloadUrlService _urlService;

        #endregion

        #region Public Methods

        public bool LoadAll()
        {
            _versions.Clear();

            if (!Directory.Exists(_gamePathService.VersionsDir))
            {
                Loaded?.Invoke(false);
                return false;
            }

            var availableVersions =
                Directory.EnumerateDirectories(_gamePathService.VersionsDir)
                    .Select(dir => $"{dir}/{Path.GetFileName(dir)}.json")
                    .Where(jsonPath => File.Exists(jsonPath))
                    .Select(jsonPath => Load(jsonPath))
                    .Where(version => version != null);

            var inheritVersions = new List<Version>(8);

            foreach (var version in availableVersions)
            {
                _versions.Add(version.ID, version);
                if (version.InheritsFrom != null) inheritVersions.Add(version);
            }

            foreach (var version in inheritVersions) InheritParentProperties(version);

            Loaded?.Invoke(_versions.Any());
            return _versions.Any();
        }

        public bool Any()
        {
            return _versions.Any();
        }

        public bool Has(string id)
        {
            return id != null && _versions.ContainsKey(id);
        }

        public IEnumerable<Version> GetAll()
        {
            return _versions.Values.OrderBy(v => v.ID);
        }

        public Version GetByID(string id)
        {
            if (id != null && _versions.TryGetValue(id, out var version)) return version;

            return null;
        }

        public Version AddNew(string jsonPath)
        {
            var newVersion = Load(jsonPath);

            if (newVersion != null)
            {
                if (newVersion.InheritsFrom != null) InheritParentProperties(newVersion);
                _versions.Add(newVersion.ID, newVersion);
                Created?.Invoke(newVersion);
            }
            else
            {
                // This version is invalid, cleanup
                File.Delete(jsonPath);
                Directory.Delete(Path.GetDirectoryName(jsonPath));
            }

            return newVersion;
        }

        public async Task DeleteFromDiskAsync(string id, bool isDeleteLibs)
        {
            if (_versions.TryGetValue(id, out var versionToDelete))
            {
                _versions.Remove(id);
                Deleted?.Invoke(versionToDelete);

                // Delete version directory
                await SystemUtil.SendDirToRecycleBinAsync($"{_gamePathService.VersionsDir}/{id}");

                // Delete unused libraries
                if (isDeleteLibs)
                {
                    var libsToDelete = versionToDelete.Libraries as IEnumerable<Library>;

                    if (_versions.Any())
                        foreach (var version in _versions.Values)
                            libsToDelete = libsToDelete.Except(version.Libraries);

                    foreach (var lib in libsToDelete)
                    {
                        string libPath = $"{_gamePathService.LibrariesDir}/{lib.Path}";

                        if (lib.Type == LibraryType.ForgeMain)
                        {
                            await SystemUtil.SendDirToRecycleBinAsync(Path.GetDirectoryName(libPath));
                        }
                        else
                        {
                            await SystemUtil.SendFileToRecycleBinAsync(libPath);
                            SystemUtil.DeleteEmptyDirs(Path.GetDirectoryName(libPath));
                        }
                    }
                }
            }
        }

        public async ValueTask<IEnumerable<VersionDownload>> GetDownloadListAsync()
        {
            try
            {
                var json = await _client.GetByteArrayAsync(_urlService.Base.VersionList);
                var versionList = JsonSerializer.Deserialize<JVersionList>(json);

                return versionList.versions.Select(download =>
                    new VersionDownload
                    {
                        ID = download.id,
                        Url = download.url[32..],
                        ReleaseTime = download.releaseTime,
                        Type = download.type == "release" ? VersionType.Release : VersionType.Snapshot
                    });
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine(ex.ToString());
                return null;
            }
            catch (OperationCanceledException)
            {
                // AuthTimeout
                Debug.WriteLine("[ERROR] Get version download list timeout");
                return null;
            }
        }

        public async ValueTask<byte[]> GetJsonAsync(VersionDownload download)
        {
            try
            {
                return await _client.GetByteArrayAsync(_urlService.Base.Json + download.Url)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                Debug.WriteLine(ex.ToString());
                return null;
            }
            catch (OperationCanceledException)
            {
                // AuthTimeout
                Debug.WriteLine("[ERROR] Get version download list timeout");
                return null;
            }
        }

        public bool CheckIntegrity(Version version)
        {
            string jarPath = $"{_gamePathService.VersionsDir}/{version.JarID}/{version.JarID}.jar";
            return File.Exists(jarPath) && CryptUtil.GetFileSHA1(jarPath) == version.SHA1;
        }

        public IEnumerable<DownloadItem> GetDownload(Version version)
        {
            var item = new DownloadItem
            {
                Name = version.JarID + "jar",
                Path = $"{_gamePathService.VersionsDir}/{version.JarID}/{version.JarID}.jar",
                Url = _urlService.Base.Version + version.Url,
                Size = version.Size,
                IsCompleted = false,
                DownloadedBytes = 0
            };

            return new[] { item };
        }

        #endregion

        #region Private Methods

        private static Version Load(string jsonPath)
        {
            JVersion jver;
            try
            {
                var json = SystemUtil.ReadUtf8File(jsonPath);
                jver = JsonSerializer.Deserialize<JVersion>(json);
            }
            catch
            {
                return null;
            }

            if (!IsValidVersion(jver)) return null;

            var version = new Version
            {
                ID = jver.id,
                JarID = jver.jar ?? jver.id,
                Size = jver.downloads?.client.size ?? 0,
                SHA1 = jver.downloads?.client.sha1,
                Url = jver.downloads?.client.url[28..],
                InheritsFrom = jver.inheritsFrom,
                MainClass = jver.mainClass,
                Libraries = new List<Library>(),
                Type = jver.type == "release" ? VersionType.Release : VersionType.Snapshot
            };

            // For 1.13+ versions
            var args = jver.arguments?.game
                           .Where(element => element.ValueKind == JsonValueKind.String)
                           .Select(element => element.GetString())
                           .ToArray() ?? jver.minecraftArguments.Split(' ');

            version.MinecarftArgsDict = Enumerable.Range(0, args.Length / 2)
                .ToDictionary(i => args[i * 2], i => args[i * 2 + 1]);

            if (version.MinecarftArgsDict.TryGetValue("--tweakClass", out string tweakClass))
            {
                if (tweakClass.EndsWith("FMLTweaker"))
                {
                    // For 1.7.2 and earlier forge version, there's no 'inheritsFrom' property
                    // So it needs to be assigned in order to launch correctly
                    version.InheritsFrom ??= version.ID.Split('-')[0];
                    version.Type = VersionType.Forge;
                }

                if (tweakClass == "optifine.OptiFineTweaker") version.Type = VersionType.OptiFine;
            }

            if (version.MainClass == "cpw.mods.modlauncher.Launcher") version.Type = VersionType.NewForge;

            if (version.MainClass == "net.fabricmc.loader.launch.knot.KnotClient") version.Type = VersionType.Fabric;

            foreach (var jlib in jver.libraries)
            {
                if (jlib.name.StartsWith("tv.twitch"))
                    // Totally unnecessary libs, game can run without them
                    // Ironically, these libs only appear in 1.7.10 - 1.8.9, not found in latter versions' json :D
                    // Also can cause troubles latter (and I don't wanna deal with that particular scenario)
                    // Might as well just ignore them! (Yes, I'm slacking off, LOL)
                    continue;

                var names = jlib.name.Split(':');
                if (names.Length != 3 || !IsLibAllowed(jlib.rules)) continue;

                if (jlib.natives == null)
                {
                    var libInfo = jlib.downloads?.artifact;
                    var lib = new Library
                    {
                        Name = jlib.name,
                        Path = libInfo?.path ??
                               string.Format("{0}/{1}/{2}/{1}-{2}.jar", names[0].Replace('.', '/'), names[1], names[2]),
                        Size = libInfo?.size ?? 0,
                        SHA1 = libInfo?.sha1
                    };

                    if (names[0] == "net.minecraftforge" && names[1] == "forge")
                    {
                        lib.Type = LibraryType.ForgeMain;
                        lib.Url = $"{names[2]}/forge-{names[2]}-universal.jar";
                    }
                    else if (jlib.downloads?.artifact.url.StartsWith("https://files.minecraftforge.net/maven/") ??
                             false ||
                             jlib.url == "http://files.minecraftforge.net/maven/")
                    {
                        lib.Type = LibraryType.Forge;
                        lib.Url = jlib.downloads?.artifact.url[39..];
                    }
                    else if (jlib.url == "https://maven.fabricmc.net/")
                    {
                        lib.Type = LibraryType.Fabric;
                        lib.Url = jlib.url + lib.Path;
                    }
                    else
                    {
                        lib.Type = LibraryType.Minecraft;
                        lib.Url = jlib.downloads?.artifact.url[32..];
                    }

                    version.Libraries.Add(lib);
                }
                else
                {
                    string suffix = jlib.natives["windows"];
                    if (suffix.EndsWith("${arch}")) suffix = suffix.Replace("${arch}", "64");

                    var nativeLibInfo = jlib.downloads?.classifiers[suffix];

                    version.Libraries.Add(new Library
                    {
                        Name = $"{names[1]}-{names[2]}-{suffix}",
                        Type = LibraryType.Native,
                        Path = nativeLibInfo?.path ??
                               string.Format("{0}/{1}/{2}/{1}-{2}-{3}.jar",
                                   names[0].Replace('.', '/'), names[1], names[2], suffix),
                        Size = nativeLibInfo?.size ?? 0,
                        SHA1 = nativeLibInfo?.sha1,
                        Exclude = jlib.extract?.exclude
                    });
                }
            }

            if (version.InheritsFrom == null)
            {
                version.AssetsInfo = new AssetsInfo
                {
                    ID = jver.assetIndex.id,
                    IndexSize = jver.assetIndex.size,
                    IndexUrl = jver.assetIndex.url,
                    IndexSHA1 = jver.assetIndex.sha1,
                    TotalSize = jver.assetIndex.totalSize,
                    IsLegacy = jver.assetIndex.id == "legacy",
                };
            }

            return version;
        }

        private static bool IsValidVersion(JVersion jver)
        {
            return !string.IsNullOrWhiteSpace(jver.id)
                   && !(string.IsNullOrWhiteSpace(jver.minecraftArguments) && jver.arguments == null)
                   && !string.IsNullOrWhiteSpace(jver.mainClass)
                   && jver.libraries != null;
        }

        private static bool IsLibAllowed(List<JRule> rules)
        {
            if (rules == null) return true;

            var isAllowed = false;
            foreach (var rule in rules)
            {
                if (rule.os == null)
                {
                    isAllowed = rule.action == "allow";
                    continue;
                }

                if (rule.os.name == "windows") isAllowed = rule.action == "allow";
            }

            return isAllowed;
        }

        private void InheritParentProperties(Version version)
        {
            if (_versions.TryGetValue(version.InheritsFrom, out var parent))
            {
                version.JarID = parent.JarID;

                foreach (var arg in parent.MinecarftArgsDict)
                    if (!version.MinecarftArgsDict.ContainsKey(arg.Key))
                        version.MinecarftArgsDict.Add(arg.Key, arg.Value);

                version.Libraries = version.Libraries.Union(parent.Libraries).ToList();
                version.AssetsInfo = parent.AssetsInfo;
                version.Size = parent.Size;
                version.SHA1 = parent.SHA1;
                version.Url = parent.Url;
            }
            else
            {
                _versions.Remove(version.ID);
            }
        }
    }

    #endregion
}