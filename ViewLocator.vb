Imports System
Imports Avalonia.Controls
Imports Avalonia.Controls.Templates
Imports Avalonia.Media
Imports FerrumPix.ViewModels

Public Class ViewLocator
    Implements IDataTemplate

    Public Function Build(param As Object) As Control Implements IDataTemplate.Build
        Dim fullName = param.GetType().FullName
        If fullName Is Nothing Then Return New TextBlock With {.Text = "Typ-Fehler"}

        Dim viewName = fullName.Replace("FerrumPix.ViewModels.", "FerrumPix.Views.").Replace("ViewModel", "View")
        Dim viewType = Type.GetType(viewName)
        If viewType IsNot Nothing Then
            Return CType(Activator.CreateInstance(viewType), Control)
        End If
        Return New TextBlock With {
            .Text = "View nicht gefunden: " & viewName,
            .Foreground = New SolidColorBrush(Color.Parse("#FF6060"))
        }
    End Function

    Public Function Match(data As Object) As Boolean Implements IDataTemplate.Match
        Return TypeOf data Is ViewModelBase
    End Function
End Class
