Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input.Platform
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Der Dialog "Protokoll anzeigen". Die Logik steht in
    ''' MainWindowViewModelDiagnosticLog; hier nur das Kopieren, denn die Zwischenablage gehoert
    ''' dem Fenster.</summary>
    Public Class DiagnosticLogDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ''' <summary>Der Hinweis "Kopiert" gilt nur bis zum Schliessen, sonst stuende er beim
        ''' naechsten Oeffnen schon da.</summary>
        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property Is IsVisibleProperty Then
                Dim note = Me.FindControl(Of TextBlock)("CopiedNote")
                If note IsNot Nothing Then note.IsVisible = False
            End If
        End Sub

        ''' <summary>Kopiert die Markierung, oder ohne Markierung alles Gezeigte.
        ''' Async Sub: das eigene Try haelt eine Ausnahme vom Dispatcher fern.</summary>
        Private Async Sub OnCopyClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = TryCast(DataContext, MainWindowViewModel)
                Dim clipboard = TopLevel.GetTopLevel(Me)?.Clipboard
                If vm Is Nothing OrElse clipboard Is Nothing Then Return
                Dim box = Me.FindControl(Of TextBox)("LogText")
                Dim selected = If(box?.SelectedText, "")
                Await clipboard.SetTextAsync(If(selected.Length > 0, selected, vm.DialogLogText))
                Dim note = Me.FindControl(Of TextBlock)("CopiedNote")
                If note IsNot Nothing Then note.IsVisible = True
            Catch ex As System.Exception
                DiagnosticLogService.LogException("Diagnostics.LogCopy", ex)
            End Try
        End Sub

    End Class

End Namespace
