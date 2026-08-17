using CommunityToolkit.Mvvm.ComponentModel;
using MyApp.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;

namespace MyApp.Models
{
    public partial class ArchiveRoot : ObservableObject
    {
        
        public ArchiveRoot(string DirPath) { 
        
            RootPath = DirPath;
            _Categories.Add(_Default);       
        }


        [ObservableProperty]
        private string _RootPath = AppContext.BaseDirectory; 
                 
        private  ObservableCollection<FileCategory> _Categories = new ObservableCollection<FileCategory>();
                
        private  ObservableCollection<ArchivedFile> _Files = new ObservableCollection<ArchivedFile>();

        private readonly FileCategory _Default = new FileCategory();

        public ObservableCollection<FileCategory> Categories
        {
            get
            {
                return _Categories;
            }
        }

        
    }
}
