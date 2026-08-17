using CommunityToolkit.Mvvm.ComponentModel;
using MyApp.Models;
using System.Collections.ObjectModel;
using System.Reflection;

namespace File_Management_Tool.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public string Greeting { get; } = "Welcome to Avalonia!";
        private ArchiveRoot  _Arch;

       public MainWindowViewModel() {

            AllCategories = new ObservableCollection<FileCategory>();
            _Arch = new ArchiveRoot(Assembly.GetExecutingAssembly().Location);
             

        }

        [ObservableProperty]
        private ObservableCollection<FileCategory> _AllCategories; 
         
        public async void BuildList()
        {
            foreach (FileCategory C in _Arch.Categories)
            {
                AllCategories.Add(C);
            }
        }

    }
}
