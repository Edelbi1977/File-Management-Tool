using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using MyApp.Models;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace File_Management_Tool;

public partial class CatManagement : Window
{

    public ObservableCollection<FileCategory> _LocalCat;

    //public ObservableCollection<FileCategory> Categoris => ArchiveStore.CurrentRoot.Categories;

    public CatManagement()
    {

        _LocalCat = ArchiveStore.CurrentRoot.Categories;
        InitializeComponent();
        LoadItems();


    }


    void LoadItems()
    {
        DGCat.ItemsSource = ArchiveStore.CurrentRoot.Categories;

    }

    private async void AddCategory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        {
            if (!string.IsNullOrEmpty(TbCategory.Text))
            {
                FileCategory Item = new FileCategory(TbCategory.Text);
                if (!ArchiveStore.CurrentRoot.Categories.Contains(Item))
                {
                    ArchiveStore.CurrentRoot.Categories.Add(Item);
                    TbCategory.Text = "";
                }
               
            }
        }


    }

    void RefreshView()
    {

        DgExt.ItemsSource = null;
        if (SelectedCat is null)
        {
            BtNewExt.IsEnabled = false;
            return;
        }
        CatTitle.Text = SelectedCat.Name;
        DgExt.ItemsSource = SelectedCat.Extensions;
        BtNewExt.IsEnabled = true;

    }

    private void DataGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {

        RefreshView();

    }

    private FileCategory? SelectedCat 
    {
        get
        {
            try
            {
                FileCategory X = (FileCategory)DGCat.SelectedItem;
                return X;
            }
            catch { 
                return null;    
            }
        }
    }

    private void Button_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
           
            if (string.IsNullOrEmpty(TbNewExt.Text)) { return; } else 
            {
                ArchivedFileExt Item = new ArchivedFileExt(TbNewExt.Text);
                if (SelectedCat is null) { return; }
                if (!SelectedCat.Extensions.Contains(Item))
                {
                    SelectedCat.Extensions.Add(Item);
                   TbNewExt.Text = "";
                    RefreshView();

                }
                                        
            }

               } catch (Exception Ex){
            string msg = Ex.Message;
        }
        

    }
}