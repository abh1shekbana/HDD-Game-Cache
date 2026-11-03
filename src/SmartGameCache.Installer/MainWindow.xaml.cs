using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace SmartGameCache.Installer
{
    public partial class MainWindow : Window
    {
        private bool _isCompleted = false;

        public MainWindow()
        {
            InitializeComponent();

            string defaultPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "SmartGameCache");
            TxtInstallPath.Text = defaultPath;
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select Installation Directory",
                InitialDirectory = TxtInstallPath.Text
            };

            if (dialog.ShowDialog() == true)
            {
                TxtInstallPath.Text = dialog.FolderName;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private async void BtnAction_Click(object sender, RoutedEventArgs e)
        {
            if (_isCompleted)
            {
                if (ChkLaunchAfter.IsChecked == true)
                {
                    string exePath = Path.Combine(TxtInstallPath.Text, "SmartGameCache.exe");
                    if (File.Exists(exePath))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            UseShellExecute = true
                        });
                    }
                }
                Close();
                return;
            }

            string targetDir = TxtInstallPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                MessageBox.Show("Please choose a valid destination folder.", "Invalid Path", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BtnAction.IsEnabled = false;
            BtnBrowse.IsEnabled = false;
            TxtInstallPath.IsEnabled = false;
            ChkDesktopShortcut.IsEnabled = false;
            ChkLaunchAfter.IsEnabled = false;

            try
            {
                await InstallAsync(targetDir);

                _isCompleted = true;
                BtnAction.Content = "Finish";
                BtnAction.IsEnabled = true;
                BtnCancel.Visibility = Visibility.Collapsed;
                TxtStatus.Text = "Installation completed successfully!";
                PrgInstall.Value = 100;
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Error: {ex.Message}";
                MessageBox.Show($"Installation failed:\n\n{ex.Message}", "Installation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                BtnAction.IsEnabled = true;
                BtnCancel.IsEnabled = true;
            }
        }

        private async Task InstallAsync(string targetDir)
        {
            TxtStatus.Text = "Preparing installation directory...";
            PrgInstall.Value = 10;
            await Task.Yield();

            Directory.CreateDirectory(targetDir);

            TxtStatus.Text = "Extracting application packages...";
            PrgInstall.Value = 25;
            await Task.Yield();

            var assembly = Assembly.GetExecutingAssembly();
            // Look for Payload.zip resource
            string? resourceName = null;
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (name.EndsWith("Payload.zip", StringComparison.OrdinalIgnoreCase))
                {
                    resourceName = name;
                    break;
                }
            }

            if (resourceName == null)
            {
                throw new InvalidOperationException("Embedded installer payload not found in setup executable.");
            }

            await Task.Run(() =>
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null) throw new InvalidOperationException("Could not open embedded payload stream.");

                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                int totalEntries = archive.Entries.Count;
                int count = 0;

                foreach (var entry in archive.Entries)
                {
                    string destinationPath = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(destinationPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                        entry.ExtractToFile(destinationPath, overwrite: true);
                    }

                    count++;
                    int progress = 25 + (int)((double)count / totalEntries * 50);
                    Dispatcher.Invoke(() =>
                    {
                        PrgInstall.Value = progress;
                        TxtStatus.Text = $"Extracting {entry.Name}...";
                    });
                }
            });

            TxtStatus.Text = "Creating application shortcuts...";
            PrgInstall.Value = 85;
            await Task.Yield();

            string exePath = Path.Combine(targetDir, "SmartGameCache.exe");

            // Start Menu Shortcut
            string startMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            string startMenuShortcut = Path.Combine(startMenuPrograms, "Smart Game Cache.lnk");
            CreateShortcut(startMenuShortcut, exePath, targetDir);

            // Desktop Shortcut
            if (ChkDesktopShortcut.IsChecked == true)
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string desktopShortcut = Path.Combine(desktop, "Smart Game Cache.lnk");
                CreateShortcut(desktopShortcut, exePath, targetDir);
            }

            TxtStatus.Text = "Registering with Windows...";
            PrgInstall.Value = 95;
            await Task.Yield();

            RegisterInUninstall(targetDir, exePath);
        }

        private static void CreateShortcut(string shortcutPath, string targetExe, string workDir)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    dynamic shell = Activator.CreateInstance(shellType)!;
                    dynamic shortcut = shell.CreateShortcut(shortcutPath);
                    shortcut.TargetPath = targetExe;
                    shortcut.WorkingDirectory = workDir;
                    shortcut.Description = "Smart Game Cache - Intelligent HDD Gaming Accelerator";
                    shortcut.Save();
                }
            }
            catch
            {
                // Fallback: non-fatal if shortcut creation is blocked by policy
            }
        }

        private static void RegisterInUninstall(string installDir, string exePath)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\SmartGameCache");
                if (key != null)
                {
                    key.SetValue("DisplayName", "Smart Game Cache");
                    key.SetValue("DisplayVersion", "1.0.0");
                    key.SetValue("Publisher", "Smart Game Cache Systems");
                    key.SetValue("InstallLocation", installDir);
                    key.SetValue("DisplayIcon", exePath);
                    key.SetValue("UninstallString", $"cmd.exe /c rmdir /s /q \"{installDir}\"");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                }
            }
            catch
            {
                // Non-fatal
            }
        }
    }
}

