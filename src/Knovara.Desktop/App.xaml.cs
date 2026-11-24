using System.Windows;

namespace Knovara.Desktop
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            var login = new LoginWindow();
            login.Show();
        }
    }
}
