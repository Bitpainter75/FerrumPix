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

        ''' <summary>Die Zeilen und Stichwort-Chips entstehen erst aus ihrem Template, nach dem
        ''' einmaligen Durchlauf der Uebersetzung; ihre festen Texte (Kurzhinweise der Knoepfe)
        ''' blieben sonst in der Ausgangssprache.</summary>
        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Avalonia.Visual)
            If itemRoot IsNot Nothing Then FerrumPix.Services.LocalizationService.ApplyToMaterialized(itemRoot)
        End Sub

    End Class

End Namespace
