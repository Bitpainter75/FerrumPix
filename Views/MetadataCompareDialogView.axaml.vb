Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Views

    ''' <summary>Der Dialog "Metadaten vergleichen": Datei und Katalog fuer ein Bild nebeneinander,
    ''' mit Kopieren und Loeschen je Angabe (MetadataCompareViewModel).</summary>
    Public Class MetadataCompareDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

    End Class

End Namespace
