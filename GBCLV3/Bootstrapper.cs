using GBCLV3.Models;
using GBCLV3.Services;
using GBCLV3.Services.Authentication;
using GBCLV3.Services.Auxiliary;
using GBCLV3.Services.Download;
using GBCLV3.Services.Installation;
using GBCLV3.Services.Launch;
using GBCLV3.Utils;
using GBCLV3.ViewModels.Tabs;
using GBCLV3.ViewModels.Windows;
using GBCLV3.Views.Windows;
using Stylet;
using StyletIoC;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GBCLV3.Models.Authentication;
using GBCLV3.ViewModels;

namespace GBCLV3
{
    internal class Bootstrapper : Bootstrapper<MainViewModel>
    {
        protected override void ConfigureIoC(IStyletIoCBuilder builder)
        {
            // Configure the IoC container in here
            builder.Bind<ConfigService>().ToSelf().InSingletonScope();
            builder.Bind<LanguageService>().ToSelf().InSingletonScope();
            builder.Bind<UpdateService>().ToSelf().InSingletonScope();
            builder.Bind<ThemeService>().ToSelf().InSingletonScope();

            builder.Bind<GamePathService>().ToSelf().InSingletonScope();
            builder.Bind<DownloadUrlService>().ToSelf().InSingletonScope();
            builder.Bind<VersionService>().ToSelf().InSingletonScope();
            builder.Bind<LibraryService>().ToSelf().InSingletonScope();
            builder.Bind<AssetService>().ToSelf().InSingletonScope();
            builder.Bind<AuthService>().ToSelf().InSingletonScope();
            builder.Bind<AccountService>().ToSelf().InSingletonScope();
            builder.Bind<LaunchService>().ToSelf().InSingletonScope();
            builder.Bind<ForgeInstallService>().ToSelf().InSingletonScope();
            builder.Bind<FabricInstallService>().ToSelf().InSingletonScope();
            builder.Bind<ModService>().ToSelf().InSingletonScope();
            builder.Bind<ResourcePackService>().ToSelf().InSingletonScope();
            builder.Bind<SkinService>().ToSelf().InSingletonScope();

            // To share these two VMs among other VMs, they must be singletons
            builder.Bind<VersionsManagementViewModel>().ToSelf().InSingletonScope();
            builder.Bind<GreetingViewModel>().ToSelf().InSingletonScope();

            // Custom MessageBox
            builder.Bind<IMessageBoxViewModel>().To<MessageViewModel>();

            // Validation
            builder.Bind(typeof(IModelValidator<>)).ToAllImplementations();
        }

        protected override void OnStart()
        {
            base.OnStart();
        }

        protected override void Configure()
        {
            // Apply settings before start

            var configService = this.Container.Get<ConfigService>();
            configService.Load();

            var langService = this.Container.Get<LanguageService>();

            // Unable to inject the service using IoC, have to make it a static property
            LocalizedDescriptionAttribute.LanguageService = langService;

            // Override AccentColors
            var accentColor = NativeUtil.GetSystemColorByName("ImmersiveStartSelectionBackground");
            Application.Current.Resources[AdonisUI.Colors.AccentColor] = accentColor;

            Application.Current.Resources[AdonisUI.Colors.AccentInteractionColor]
                = NativeUtil.GetSystemColorByName("ImmersiveStartBackground");

            Application.Current.Resources[AdonisUI.Colors.AccentIntenseHighlightColor]
                = NativeUtil.GetSystemColorByName("ImmersiveStartFolderBackground");

            Application.Current.Resources[AdonisUI.Colors.DisabledAccentForegroundColor]
                = NativeUtil.GetSystemColorByName("ImmersiveStartDisabledText");

            Application.Current.Resources[AdonisUI.Colors.AccentHighlightColor]
                = NativeUtil.GetSystemColorByName("ImmersiveStartSecondaryText");

            // Update background image
            var themeService = this.Container.Get<ThemeService>();
            themeService.UpdateBackgroundImage();

            // Why load the background icon needs accent color?
            // Well...you'll see
            themeService.LoadBackgroundIcon(accentColor);
        }

        protected override void OnLaunch()
        {
            if (this.Args.Any() && this.Args[0] == "updated")
            {
                var windowManager = this.Container.Get<IWindowManager>();
                windowManager.ShowMessageBox("${SuccessfullyUpdatedTo} v" + AssemblyUtil.Version, "${UpdateSuccessful}",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                File.Delete("GBCL.old");
            }
            else
            {
                var configService = this.Container.Get<ConfigService>();
                if (configService.Entries.AutoCheckUpdate)
                {
                    CheckUpdateAsync().ConfigureAwait(false);
                }
            }

            CheckAccount();
            CheckAuthlibInjectorUpdate().ConfigureAwait(false);
            base.OnLaunch();
        }

        protected override void OnUnhandledException(DispatcherUnhandledExceptionEventArgs e)
        {
            var windowManager = this.Container.Get<IWindowManager>();
            var errorReportVM = this.Container.Get<ErrorReportViewModel>();

            errorReportVM.ErrorMessage = e.Exception.ToString();
            errorReportVM.Type = ErrorReportType.UnhandledException;
            windowManager.ShowDialog(errorReportVM);

            this.Application.Shutdown(e.Exception.HResult);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            var configService = this.Container.Get<ConfigService>();
            configService.Save();
            base.OnExit(e);
        }

        private async Task CheckUpdateAsync()
        {
            var updateService = this.Container.Get<UpdateService>();
            var info = await updateService.CheckAsync();

            if (info != null)
            {
                var updateVM = this.Container.Get<UpdateViewModel>();
                var windowManager = this.Container.Get<IWindowManager>();
                updateVM.Setup(info);
                windowManager.ShowWindow(updateVM);
            }
        }

        private void CheckAccount()
        {
            var accountService = this.Container.Get<AccountService>();
            if (!accountService.Any())
            {
                var windowManager = this.Container.Get<IWindowManager>();
                var accountEditVM = this.Container.Get<AccountEditViewModel>();
                accountEditVM.Setup(AccountEditType.AddAccount);
                windowManager.ShowDialog(accountEditVM);
            }
        }

        private async Task CheckAuthlibInjectorUpdate()
        {
            var accountService = this.Container.Get<AccountService>();
            var selectedAccount = accountService.GetSelected();

            if (selectedAccount?.AuthMode != AuthMode.AuthLibInjector) return;

            var authlibInjectorService = this.Container.Get<AuthlibInjectorService>();
            int localBuild = authlibInjectorService.GetLocalBuild();
            var latest = await authlibInjectorService.GetLatest().ConfigureAwait(false);

            if (localBuild < latest.Build)
            {
                var download = authlibInjectorService.GetDownload(latest);
                using var downloadService = new DownloadService(download);
                await downloadService.StartAsync().ConfigureAwait(false);
            }
        }
    }
}