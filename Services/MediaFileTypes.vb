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
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp",
            ".heic", ".heif", ".hif", ".avif", ".jxl",
            ".ico", ".svg", ".fpx", ".psd", ".psb",
            ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"
        }.Concat(RawPreviewService.SupportedExtensions).ToArray()

        ''' <summary>Die Videoformate unter <see cref="Displayable"/>.</summary>
        Public Shared ReadOnly VideoExtensions As String() = {".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"}

        ''' <summary>Ist die Datei ein Video? Nach der Endung, kleingeschrieben verglichen.</summary>
        Public Shared Function IsVideo(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Return VideoExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())
        End Function

        ''' <summary>Zeigt die Anwendung diese Datei? Vergleicht kleingeschrieben, weil eine Endung
        ''' auf der Platte in jeder Schreibweise stehen kann.</summary>
        Public Shared Function IsDisplayable(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Return Displayable.Contains(Path.GetExtension(filePath).ToLowerInvariant())
        End Function

        ''' <summary>Der Name mit kleingeschriebener Endung ("IMG_0001.JPG" wird "IMG_0001.jpg").
        ''' Gilt fuer jeden Namen, den die Anwendung NEU vergibt: Speichern unter, Exportieren,
        ''' Stapel-Konvertieren, Umbenennen und Stapel-Umbenennen. Der Stamm bleibt, wie er ist.
        ''' Eine Datei, die an Ort und Stelle ueberschrieben wird, behaelt ihren Namen; ebenso eine
        ''' Kopie oder ein Verschieben, das ist dieselbe Datei an anderer Stelle.</summary>
        Public Shared Function WithLowerCaseExtension(fileName As String) As String
            If String.IsNullOrEmpty(fileName) Then Return fileName
            ' Nur eine bekannte Endung: in "foto-f3.5_MG" ist ".5_MG" Teil des Namens.
            Dim extension = KnownExtension(fileName)
            If extension.Length = 0 OrElse String.Equals(extension, extension.ToLowerInvariant(), StringComparison.Ordinal) Then Return fileName
            Return fileName.Substring(0, fileName.Length - extension.Length) & extension.ToLowerInvariant()
        End Function

        ''' <summary>Was ausser den Medien noch als Endung eines vergebenen Namens gilt: PDF ist ein
        ''' Ziel von Speichern unter, wird aber nicht angezeigt.</summary>
        Private Shared ReadOnly ExtraTargetExtensions As String() = {".pdf"}

        ''' <summary>Die Endung des Namens, wenn sie ein Format ist, das die Anwendung kennt
        ''' (<see cref="Displayable"/> oder ein Ziel wie PDF); sonst leer.
        '''
        ''' <c>Path.GetExtension</c> haelt alles nach dem letzten Punkt fuer eine Endung. Fuer einen
        ''' Namen, den ein Nutzer tippt oder ein Muster bildet, ist das falsch: Blende und Brennweite
        ''' stehen oft mit Punkt darin ("Nikkor_5.8cm-f1.4"). Wer dort am Punkt abschnitt, kuerzte
        ''' den Namen; wer dort keine Endung anhaengte, weil schon eine "da" war, schrieb eine Datei
        ''' ohne ihre Endung.</summary>
        Public Shared Function KnownExtension(fileName As String) As String
            If String.IsNullOrEmpty(fileName) Then Return ""
            Dim extension = Path.GetExtension(fileName)
            If extension.Length = 0 Then Return ""
            Dim lower = extension.ToLowerInvariant()
            Return If(Displayable.Contains(lower) OrElse ExtraTargetExtensions.Contains(lower), extension, "")
        End Function

        ''' <summary>Der Name ohne seine Endung, aber nur, wenn es eine bekannte ist (siehe
        ''' <see cref="KnownExtension"/>). Ein Punkt mitten im Namen bleibt stehen.</summary>
        Public Shared Function WithoutKnownExtension(fileName As String) As String
            If String.IsNullOrEmpty(fileName) Then Return If(fileName, "")
            Dim extension = KnownExtension(fileName)
            Return fileName.Substring(0, fileName.Length - extension.Length)
        End Function

    End Class

End Namespace
