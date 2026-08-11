Imports System
Imports System.IO
Imports System.Linq

Namespace Services

    ''' <summary>
    ''' DIE Liste der Endungen, die FerrumPix als Medium anzeigt. Alle anderen Stellen leiten davon
    ''' ab, statt eigene Kopien zu fuehren - wie bei den RAW-Endungen, deren kanonische Liste in
    ''' <see cref="RawPreviewService.SupportedExtensions"/> steht und hier mit hineinkommt.
    '''
    ''' Sie stand bis dahin als privates Feld in der Galerie. Das reichte, solange nur die Galerie
    ''' Ordner durchging; der Katalogindex geht dieselben Ordner durch, und zwei Listen davon
    ''' driften zwangslaeufig auseinander - der Index haette dann Dateien aufgenommen, die die
    ''' Galerie nie zeigt, oder umgekehrt welche uebersehen, die sie zeigt.
    ''' </summary>
    Public NotInheritable Class MediaFileTypes

        Private Sub New()
        End Sub

        ''' <summary>Feste Formate plus die kanonischen RAW-Endungen.
        '''
        ''' ".fpx" gehoert dazu: FerrumPix-Projekte erscheinen wie Bilder in Galerie und Filmstreifen
        ''' (Vorschaubild aus dem eingebetteten Composite, siehe ThumbnailCacheService).</summary>
        Public Shared ReadOnly Displayable As String() = {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".heic", ".avif",
            ".ico", ".svg", ".fpx", ".psd", ".psb",
            ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"
        }.Concat(RawPreviewService.SupportedExtensions).ToArray()

        ''' <summary>Zeigt die Anwendung diese Datei? Vergleicht kleingeschrieben, weil eine Endung
        ''' auf der Platte in jeder Schreibweise stehen kann.</summary>
        Public Shared Function IsDisplayable(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Return Displayable.Contains(Path.GetExtension(filePath).ToLowerInvariant())
        End Function

    End Class

End Namespace
