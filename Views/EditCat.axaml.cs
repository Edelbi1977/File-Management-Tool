using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using MyApp.Models;

namespace File_Management_Tool;

public partial class EditCat : Window
{
    private FileCategory _category;

    public EditCat(FileCategory Src)
    {
        _category = Src;
        InitializeComponent();
    }



}