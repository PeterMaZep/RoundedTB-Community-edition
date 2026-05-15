using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Navigation;
using System.Diagnostics;

namespace RoundedTB
{
    /// <summary>
    /// Interaction logic for AboutWindow.xaml
    /// </summary>
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();

            Icon = new BitmapImage(new Uri(Branding.IconResourcePath));

            // The three banners start Hidden/Hidden/Visible in XAML; show the one that
            // matches the build variant so About's hero image matches the titlebar icon.
            // Using #if avoids CS0162 warnings from a switch over a const.
            bannerMst.Visibility = Visibility.Hidden;
            bannerDev.Visibility = Visibility.Hidden;
            bannerCan.Visibility = Visibility.Hidden;
#if DEBUG
            bannerDev.Visibility = Visibility.Visible;
            subtitleBlock.Text = "Community Edition (Dev build)";
#elif RTB_RELEASE
            bannerMst.Visibility = Visibility.Visible;
            subtitleBlock.Text = "Community Edition v0.4.1";
#else
            bannerCan.Visibility = Visibility.Visible;
            subtitleBlock.Text = "Community Edition (Canary)";
#endif
        }

        private void okButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            OpenWithShell(e.Uri.ToString());
            e.Handled = true;
        }

        private void configButton_Click(object sender, RoutedEventArgs e)
        {
            OpenWithShell(((MainWindow)Application.Current.MainWindow).configPath);
        }

        private void logButton_Click(object sender, RoutedEventArgs e)
        {
            OpenWithShell(((MainWindow)Application.Current.MainWindow).logPath);
        }

        // .NET (Core) defaults Process.Start(string) to UseShellExecute=false,
        // which tries to exec the path as an .exe and throws for URLs or
        // non-executable files. Route through ShellExecute so the associated
        // handler (browser for URLs, text editor for .json/.log) opens.
        private static void OpenWithShell(string target)
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
    }
}
