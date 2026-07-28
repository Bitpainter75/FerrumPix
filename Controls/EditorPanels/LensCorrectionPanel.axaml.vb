Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Controls.EditorPanels

    ''' <summary>Ohne Code-behind: die Objektivwahl laeuft ueber Bindungen und ein Kommando. Eine
    ''' fruehere Fassung fing das Auswaehlen selbst ab und stuerzte damit in Avaloniens Abbau des
    ''' Aufklapp-Fensters ab - siehe den Kommentar am Suchfeld.</summary>
    Public Class LensCorrectionPanel
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
