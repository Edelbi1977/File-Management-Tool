using Avalonia.Controls;
using Avalonia.Interactivity;
using MyApp.Models;

namespace File_Management_Tool.Views
{
    public partial class MainWindow : Window
    {

        public MainWindow()
        {
           
            InitializeComponent();
        }

      

        private async void OnManageCategoriesClick(object? sender, RoutedEventArgs e)
        {
            var Cm = new CatManagement();
            await Cm.ShowDialog(this);
        }
        
    }
}