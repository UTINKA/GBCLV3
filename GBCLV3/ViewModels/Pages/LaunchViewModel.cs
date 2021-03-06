﻿using System;
using GBCLV3.Models;
using GBCLV3.Models.Download;
using GBCLV3.Models.Launch;
using GBCLV3.Services;
using GBCLV3.Services.Authentication;
using GBCLV3.Services.Download;
using GBCLV3.Services.Launch;
using GBCLV3.Utils;
using GBCLV3.ViewModels.Tabs;
using GBCLV3.ViewModels.Windows;
using Stylet;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using GBCLV3.Models.Authentication;
using GBCLV3.Services.Installation;

namespace GBCLV3.ViewModels.Pages
{
    public class LaunchViewModel : Conductor<IScreen>.Collection.OneActive
    {
        #region Events

        public event Action LaunchProcessStarted;

        public event Action LaunchProcessEnded;

        #endregion

        #region Private Fields

        private const string XD = "_(:3」∠)_";
        private readonly StringBuilder _logger = new StringBuilder(4096);

        // IoC
        private readonly Config _config;
        private readonly VersionService _versionService;
        private readonly LibraryService _libraryService;
        private readonly AssetService _assetService;
        private readonly AccountService _accountService;
        private readonly AuthService _authService;
        private readonly AuthlibInjectorService _authlibInjectorService;
        private readonly LaunchService _launchService;

        private readonly LaunchStatusViewModel _statusVM;
        private readonly DownloadStatusViewModel _downloadStatusVM;
        private readonly ErrorReportViewModel _errorReportVM;
        private readonly AccountEditViewModel _accountEditVM;
        private readonly ProfileSelectViewModel _profileSelectVM;

        private readonly IWindowManager _windowManager;

        #endregion

        #region Constructor

        public LaunchViewModel(
            ConfigService configService,
            ThemeService themeService,
            VersionService versionService,
            LibraryService libraryService,
            AssetService assetService,
            AccountService accountService,
            AuthService authService,
            AuthlibInjectorService authlibInjectorService,
            LaunchService launchService,

            IWindowManager windowManager,
            GreetingViewModel greetingVM,
            VersionsManagementViewModel versionsVM,
            LaunchStatusViewModel statusVM,
            AccountEditViewModel accountEditVM,
            DownloadStatusViewModel downloadVM,
            ErrorReportViewModel errorReportVM,
            ProfileSelectViewModel profileSelectVM)
        {
            _windowManager = windowManager;
            _config = configService.Entries;

            _versionService = versionService;
            _libraryService = libraryService;
            _assetService = assetService;
            _accountService = accountService;
            _authService = authService;
            _authlibInjectorService = authlibInjectorService;
            _launchService = launchService;

            _statusVM = statusVM;
            _accountEditVM = accountEditVM;
            _profileSelectVM = profileSelectVM;
            _downloadStatusVM = downloadVM;
            _errorReportVM = errorReportVM;

            _launchService.ErrorReceived += errorMessage => _logger.Append(errorMessage);
            _launchService.Exited += OnGameExited;

            _versionService.Loaded += hasAny => HasVersion = hasAny;
            _versionService.Created += _ => HasVersion = true;

            _statusVM.Closed += (sender, e) => OnLaunchCompleted();

            ThemeService = themeService;
            GreetingVM = greetingVM;
            VersionsVM = versionsVM;
        }

        #endregion

        #region Bindings

        public VersionsManagementViewModel VersionsVM { get; }

        public GreetingViewModel GreetingVM { get; }

        public ThemeService ThemeService { get; }

        public bool IsLaunching { get; private set; }

        public bool HasVersion { get; private set; }

        public bool CanLaunch => HasVersion && !IsLaunching;

        public async void Launch()
        {
            IsLaunching = true;
            GreetingVM.IsShown = false;
            LaunchProcessStarted?.Invoke();

            // Check JRE
            if (_config.JreDir == null)
            {
                _windowManager.ShowMessageBox("${JreNotFound}\n${PleaseInstallJre}", "${IntegrityCheck}",
                    MessageBoxButton.OK, MessageBoxImage.Error);

                _statusVM.Status = LaunchStatus.Failed;
                return;
            }

            _statusVM.GameOutputLog = null;
            this.ActivateItem(_statusVM);

            _statusVM.Status = LaunchStatus.LoggingIn;

            // No account found, the user must create one
            var account = _accountService.GetSelected();
            if (account == null)
            {
                _accountEditVM.Setup(AccountEditType.AddAccount);

                if (_windowManager.ShowDialog(_accountEditVM) != true)
                {
                    _statusVM.Status = LaunchStatus.Failed;
                    return;
                }

                account = _accountService.GetSelected();
            }
            else
            {
                // Previous login token is invalid, need re-authentication
                var authResult = await _authService.LoginAsync(account);
                if (!authResult.IsSuccessful)
                {
                    _accountEditVM.Setup(AccountEditType.ReAuth, account);

                    if (_windowManager.ShowDialog(_accountEditVM) != true)
                    {
                        _statusVM.Status = LaunchStatus.Failed;
                        return;
                    }
                }
                else if (authResult.SelectedProfile == null)
                {
                    var selectedProfile = authResult.AvailableProfiles.FirstOrDefault();

                    _profileSelectVM.Setup(authResult.AvailableProfiles, account.ProfileServer);
                    if (_windowManager.ShowDialog(_profileSelectVM) ?? false)
                    {
                        selectedProfile = _profileSelectVM.SelectedProfile;
                    }

                    account.Username = selectedProfile.Name;
                    account.UUID = selectedProfile.Id;
                }
            }

            // Check authlib-injector if selected account is using external authentication
            if (account.AuthMode == AuthMode.AuthLibInjector)
            {
                if (!_authlibInjectorService.CheckIntegrity(account.AuthlibInjectorSHA256))
                {
                    // Authlib-Injector is missing or damaged
                    var latest = await _authlibInjectorService.GetLatest();
                    if (latest == null)
                    {
                        _statusVM.Status = LaunchStatus.Failed;
                        return;
                    }

                    var download = _authlibInjectorService.GetDownload(latest);
                    if (!await StartDownloadAsync(DownloadType.AuthlibInjector, download))
                    {
                        _statusVM.Status = LaunchStatus.Failed;
                        return;
                    }

                    account.AuthlibInjectorSHA256 = latest.SHA256;
                }

                account.PrefetchedAuthServerInfo =
                    await _authService.PrefetchAuthServerInfo(account.AuthServerBase);

                if (account.PrefetchedAuthServerInfo == null)
                {
                    _statusVM.Status = LaunchStatus.Failed;
                    return;
                }
            }

            _statusVM.Status = LaunchStatus.ProcessingDependencies;
            var launchVersion = _versionService.GetByID(_config.SelectedVersion);

            // Check main jar and fix possible damage
            if (!_versionService.CheckIntegrity(launchVersion))
            {
                var download = _versionService.GetDownload(launchVersion);
                if (!await StartDownloadAsync(DownloadType.MainJar, download))
                {
                    _statusVM.Status = LaunchStatus.Failed;
                    return;
                }
            }

            // Check dependent libraries and fix possible damage
            var damagedLibs = await _libraryService.CheckIntegrityAsync(launchVersion.Libraries);
            if (damagedLibs.Any())
            {
                // For 1.13.2+ forge versions, there is no way to fix damaged forge jar unless reinstall
                if (launchVersion.Type == VersionType.NewForge && damagedLibs.Any(
                        lib => lib.Type == LibraryType.ForgeMain
                    ))
                {
                    _windowManager.ShowMessageBox("${MainJarDamaged}\n${PleaseReinstallForge}", "${IntegrityCheck}",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    // Delete the damaged forge version (but retain the libraries)
                    // force user to reinstall it
                    await _versionService.DeleteFromDiskAsync(launchVersion.ID, false);

                    _statusVM.Status = LaunchStatus.Failed;
                    return;
                }

                var downloads = _libraryService.GetDownloads(damagedLibs);
                if (!await StartDownloadAsync(DownloadType.Libraries, downloads))
                {
                    _statusVM.Status = LaunchStatus.Failed;
                    return;
                }
            }

            // Extract native libraries
            _libraryService.ExtractNatives(launchVersion.Libraries.Where(lib => lib.Type == LibraryType.Native));

            // Try loading assets
            if (!_assetService.LoadAllObjects(launchVersion.AssetsInfo))
            {
                if (await _assetService.DownloadIndexJsonAsync(launchVersion.AssetsInfo))
                {
                    // Successfully downloaded the missing index json, load assets
                    _assetService.LoadAllObjects(launchVersion.AssetsInfo);
                }

                // if index json download failed (what are the odds!), not gonna retry
                // Prepare for enjoying a silent game XD
            }

            // Check assets and fix possible damage on user's discretion
            var damagedAssets = await _assetService.CheckIntegrityAsync(launchVersion.AssetsInfo);
            if ((damagedAssets?.Any() ?? false) &&
                _windowManager.ShowMessageBox("${AssetsDamaged}\n${WhetherFixNow}", "${IntegrityCheck}",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                var downloads = _assetService.GetDownloads(damagedAssets);
                await StartDownloadAsync(DownloadType.Assets, downloads);
            }

            // For legacy versions (1.7.2 or earlier), copy hashed asset objects to virtual files
            if (launchVersion.AssetsInfo.IsLegacy)
            {
                await _assetService.CopyToVirtualAsync(launchVersion.AssetsInfo);
            }

            // All good to go, now build launch profile
            var profile = new LaunchProfile
            {
                IsDebugMode = _config.JavaDebugMode,
                JvmArgs = _config.JvmArgs,
                MaxMemory = _config.JavaMaxMem,
                Account = account,
                VersionType = AssemblyUtil.Title,
                WinWidth = _config.WindowWidth,
                WinHeight = _config.WindowHeight,
                IsFullScreen = _config.FullScreen,
                ServerAddress = _config.ServerAddress,
                ExtraArgs = _config.ExtraMinecraftArgs,
            };

            _statusVM.Status = LaunchStatus.StartingProcess;

            void UpdateLogDisplay(string logMessage) => _statusVM.GameOutputLog = logMessage;
            _launchService.LogReceived += UpdateLogDisplay;

            if (!await _launchService.LaunchGameAsync(profile, launchVersion))
            {
                _statusVM.Status = LaunchStatus.Failed;
                _launchService.LogReceived -= UpdateLogDisplay;
                return;
            }

            _statusVM.Status = LaunchStatus.Running;

            _launchService.LogReceived -= UpdateLogDisplay;
            _statusVM.GameOutputLog = XD;
        }

        #endregion

        #region Private Methods

        private async ValueTask<bool> StartDownloadAsync(DownloadType type, IEnumerable<DownloadItem> items)
        {
            _statusVM.Status = LaunchStatus.Downloading;

            using var downloadService = new DownloadService(items);
            _downloadStatusVM.Setup(type, downloadService);
            this.ActivateItem(_downloadStatusVM);

            bool isSuccessful = await downloadService.StartAsync();

            this.ActivateItem(_statusVM);
            _statusVM.Status = LaunchStatus.ProcessingDependencies;

            return isSuccessful;
        }

        private void OnLaunchCompleted()
        {
            if (_statusVM.Status == LaunchStatus.Failed)
            {
                IsLaunching = false;
                GreetingVM.IsShown = true;
                LaunchProcessEnded?.Invoke();
                return;
            }

            if (_statusVM.Status == LaunchStatus.Running)
            {
                if (_config.AfterLaunch == AfterLaunchBehavior.Exit)
                {
                    Application.Current.Shutdown(0);
                }

                if (_config.AfterLaunch == AfterLaunchBehavior.Hide)
                {
                    Application.Current.MainWindow.Hide();
                }
            }
        }

        private void OnGameExited(int exitCode)
        {
            IsLaunching = false;
            LaunchProcessEnded?.Invoke();

            Execute.OnUIThread(() =>
            {
                GreetingVM.IsShown = true;

                if (_config.AfterLaunch != AfterLaunchBehavior.Exit && exitCode != 0 && _logger.Length > 0)
                {
                    _errorReportVM.ErrorMessage = $"Exit Code: {exitCode}\n" + _logger.ToString();
                    _errorReportVM.Type = ErrorReportType.UnexpectedExit;

                    _windowManager.ShowDialog(_errorReportVM);

                    Debug.WriteLine("[Game exited with errors]");
                    Debug.WriteLine(_logger.ToString());
                    _logger.Clear();
                }

                if (_config.AfterLaunch == AfterLaunchBehavior.Hide)
                {
                    Application.Current.MainWindow.Show();
                    Application.Current.MainWindow.Activate();
                }
            });
        }

        protected override async void OnInitialActivate()
        {
            await _accountService.LoadSkinsAsync();
            GreetingVM.NotifyAccountChanged();
            GreetingVM.IsShown = true;
        }

        #endregion
    }
}