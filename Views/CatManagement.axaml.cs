using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using MyApp.Models;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace File_Management_Tool.Views;

public partial class CatManagement : Window
{

   //public ObservableCollection<FileCategory> Categoris => ArchiveStore.CurrentRoot.Categories;

    public CatManagement()
    {

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
                FileCategory item = new FileCategory(TbCategory.Text);
                if (!ArchiveStore.CurrentRoot.Categories.Contains(item))
                {
                    ArchiveStore.CurrentRoot.Categories.Add(item);
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
                var dgCatSelectedItem = (FileCategory)DGCat.SelectedItem;
                return dgCatSelectedItem;
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

            if (string.IsNullOrEmpty(TbNewExt.Text))
            {
                return;
            }
            else
            {
                ArchivedFileExt item = new ArchivedFileExt(TbNewExt.Text);
                if (SelectedCat is null)
                {
                    return;
                }

                if (!SelectedCat.Extensions.Contains(item))
                {
                    SelectedCat.Extensions.Add(item);
                    TbNewExt.Text = "";
                    RefreshView();

                }

            }

        }
        catch
        {
        }

    }
}