using CommunityToolkit.Mvvm.ComponentModel;
using MyApp.Models;
using System;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Threading.Tasks;

namespace File_Management_Tool.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        public MainWindowViewModel()
        {


        }

        [ObservableProperty]
        private string _RootPath = ArchiveStore.CurrentRoot.RootPath;

        public ObservableCollection<FileCategory> Categories => ArchiveStore.CurrentRoot.Categories;
             
       

       


    }
}
