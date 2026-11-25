Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports FerrumPix.Models
Imports MetadataExtractor
Imports MetadataExtractor.Formats.Exif.Makernotes
Imports MetadataExtractor.Formats.QuickTime

Namespace Services

    ''' <summary>Welche Art von Bewegtbild eine Datei ist. Die Werte stehen so im Katalog (Spalte
    ''' MotionKind); nicht umnummerieren.</summary>
    Public Enum MotionKind
        ''' Ein gewoehnliches Bild oder ein gewoehnlicher Film.
        None = 0
        ''' Ein JPEG mit angehaengtem Film, und der Film ist wirklich da (MotionPhotoService).
        MotionPhoto = 1
        ''' Das Standbild eines Live Photos: es traegt eine Kennung, zu der ein Film gehoeren kann.
        LivePhotoImage = 2
        ''' Der Film eines Live Photos: eine .mov mit derselben Art Kennung.
        LivePhotoVideo = 3
    End Enum

    ''' <summary>Art und Kennung einer Datei, wie der EXIF-Lauf sie liest.</summary>
    Public Structure MotionIdentity
        Public Kind As MotionKind
        ''' Die Kennung eines Live Photos; leer bei allem anderen.
        Public Id As String
    End Structure

    ''' <summary>
    ''' Live Photos: ein Standbild (HEIC oder JPEG) und daneben eine .mov mit gleichem Stamm. Was die
    ''' beiden zusammenhaelt, ist eine Kennung: im Bild im Herstellerteil des EXIF, im Film in den
    ''' Angaben des Containers. Dazu legt das Telefon oft eine .aae mit den Bearbeitungsschritten ab.
    '''
    ''' EIN PAAR IST ES NUR MIT GLEICHER KENNUNG: gleicher Ordner, gleicher Stamm, Bild und .mov,
    ''' und beide nennen dieselbe Kennung. Ohne Kennung kein Paar. Ein Film, der nur zufaellig so
    ''' heisst wie ein Foto, soll weder hinter ihm verschwinden noch mit ihm geloescht werden.
    '''
    ''' ZWEI SICHTEN AUF DASSELBE PAAR:
    ''' Die Galerie paart ihre Elemente (<see cref="PairItems"/>), ohne Dateizugriff, aus dem, was
    ''' der Katalog an die Kachel gelegt hat. Die Dateiwege ausserhalb der Galerie (Betrachter,
    ''' Editor, Ordnerbaum) fragen nach dem Pfad (<see cref="FindPartnerPath"/>): erst der Ordner,
    ''' dann der Katalog, und nur wo der nichts Aktuelles weiss, die Dateien selbst.
    ''' </summary>
    Public NotInheritable Class LivePhotoService

        Private Sub New()
        End Sub

        Private Shared ReadOnly ImageExtensions As New HashSet(Of String)(
            {".heic", ".heif", ".hif", ".jpg", ".jpeg", ".jpe", ".jfif"}, StringComparer.OrdinalIgnoreCase)

        Private Const VideoExtension As String = ".mov"

        ''' <summary>Die Bearbeitungsschritte des Telefons neben dem Bild ("IMG_0001.AAE").</summary>
        Public Const EditListExtension As String = ".aae"

        ''' <summary>Laenger ist keine Kennung (ueblich sind 36 Zeichen). Eine Datei, die hier ein
        ''' ganzes Buch ablegt, bekommt keine.</summary>
        Private Const MaximumIdLength As Integer = 128

        ''' <summary>Kann die Datei der Endung nach das Standbild eines Live Photos sein?</summary>
        Public Shared Function IsImageHalf(path As String) As Boolean
            Return Not String.IsNullOrEmpty(path) AndAlso ImageExtensions.Contains(IO.Path.GetExtension(path))
        End Function

        ''' <summary>Kann die Datei der Endung nach der Film eines Live Photos sein?</summary>
        Public Shared Function IsVideoHalf(path As String) As Boolean
            Return Not String.IsNullOrEmpty(path) AndAlso
                   String.Equals(IO.Path.GetExtension(path), VideoExtension, StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function NormalizeId(value As String) As String
            Dim id = If(value, "").Trim().Trim(ChrW(0))
            If id.Length = 0 OrElse id.Length > MaximumIdLength Then Return ""
            Return id
        End Function

        ''' <summary>Art und Kennung aus den Verzeichnissen des Metadatenlesers, wie der EXIF-Lauf
        ''' sie ohnehin hat. Ein Motion Photo geht vor: dort sagt das XMP Ja, und der Blick in die
        ''' Datei (ein Sprung, zwoelf Bytes) belegt den Film. Wirft nie.</summary>
        Public Shared Function Identify(path As String, directories As IEnumerable(Of MetadataExtractor.Directory)) As MotionIdentity
            Dim result As New MotionIdentity With {.Kind = MotionKind.None, .Id = ""}
            If String.IsNullOrEmpty(path) OrElse directories Is Nothing Then Return result
            Try
                Dim list = directories.ToList()
                If IsVideoHalf(path) Then
                    Dim id = NormalizeId(list.OfType(Of QuickTimeMetadataHeaderDirectory)().
                        Select(Function(d) d.GetString(QuickTimeMetadataHeaderDirectory.TagContentIdentifier)).
                        FirstOrDefault(Function(v) Not String.IsNullOrWhiteSpace(v)))
                    If id.Length > 0 Then
                        result.Kind = MotionKind.LivePhotoVideo
                        result.Id = id
                    End If
                    Return result
                End If
                If Not IsImageHalf(path) Then Return result

                Dim claim = MotionPhotoService.ReadClaim(list)
                If claim.Claimed Then
                    Using stream As New FileStream(path, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite Or FileShare.Delete, 4096)
                        If MotionPhotoService.Evaluate(claim, stream).HasVideo Then
                            result.Kind = MotionKind.MotionPhoto
                            Return result
                        End If
                    End Using
                End If

                Dim imageId = NormalizeId(list.OfType(Of AppleMakernoteDirectory)().
                    Select(Function(d) d.GetString(AppleMakernoteDirectory.TagContentIdentifier)).
                    FirstOrDefault(Function(v) Not String.IsNullOrWhiteSpace(v)))
                If imageId.Length > 0 Then
                    result.Kind = MotionKind.LivePhotoImage
                    result.Id = imageId
                End If
            Catch ex As Exception
                ' Eine Datei, an der die Frage scheitert, ist ein gewoehnliches Bild.
                DiagnosticLogService.LogException("LivePhoto.Identify", ex)
                result.Kind = MotionKind.None
                result.Id = ""
            End Try
            Return result
        End Function

        ''' <summary>Sind die beiden ein Live Photo? Gleicher Ordner, gleicher Stamm, ein Bild und
        ''' eine .mov, und beide mit derselben Kennung.</summary>
        Public Shared Function ArePair(firstPath As String, firstKind As MotionKind, firstId As String,
                                       secondPath As String, secondKind As MotionKind, secondId As String) As Boolean
            If String.IsNullOrEmpty(firstPath) OrElse String.IsNullOrEmpty(secondPath) Then Return False
            Dim imageAndVideo = (firstKind = MotionKind.LivePhotoImage AndAlso secondKind = MotionKind.LivePhotoVideo) OrElse
                                (firstKind = MotionKind.LivePhotoVideo AndAlso secondKind = MotionKind.LivePhotoImage)
            If Not imageAndVideo Then Return False
            If String.IsNullOrEmpty(firstId) OrElse Not String.Equals(firstId, secondId, StringComparison.OrdinalIgnoreCase) Then Return False
            If Not String.Equals(IO.Path.GetFileNameWithoutExtension(firstPath), IO.Path.GetFileNameWithoutExtension(secondPath),
                                 StringComparison.OrdinalIgnoreCase) Then Return False
            Return PathIdentity.Comparer.Equals(If(IO.Path.GetDirectoryName(firstPath), ""), If(IO.Path.GetDirectoryName(secondPath), ""))
        End Function

        ''' <summary>Kommt das Element fuer ein Paar in Frage? Nur lokale Dateien, nichts aus dem
        ''' Papierkorb, und nur mit Kennung.</summary>
        Public Shared Function IsPairCandidate(item As ImageItem) As Boolean
            Return item IsNot Nothing AndAlso Not item.IsRemoteAsset AndAlso Not item.IsTrashed AndAlso
                   Not item.IsFolder AndAlso Not item.IsVariant AndAlso Not String.IsNullOrEmpty(item.FilePath) AndAlso
                   (item.MotionKind = MotionKind.LivePhotoImage OrElse item.MotionKind = MotionKind.LivePhotoVideo) AndAlso
                   Not String.IsNullOrEmpty(item.MotionId)
        End Function

        ''' <summary>Die Live Photos einer Liste, je Element sein Partner (in beide Richtungen). Kein
        ''' Dateizugriff: gearbeitet wird mit Art und Kennung, die der Katalog an das Element gelegt
        ''' hat. Zu einem Film gehoert hoechstens EIN Bild und umgekehrt; liegen HEIC und JPEG mit
        ''' derselben Kennung daneben, bekommt die erste Datei nach dem Namen den Film.</summary>
        Public Shared Function PairItems(items As IEnumerable(Of ImageItem)) As Dictionary(Of ImageItem, ImageItem)
            Dim result As New Dictionary(Of ImageItem, ImageItem)()
            If items Is Nothing Then Return result
            Dim groups = items.Where(AddressOf IsPairCandidate).
                GroupBy(Function(i) IO.Path.Combine(If(IO.Path.GetDirectoryName(i.FilePath), ""),
                                                    IO.Path.GetFileNameWithoutExtension(i.FilePath)),
                        StringComparer.OrdinalIgnoreCase)
            For Each group In groups
                Dim files = group.OrderBy(Function(i) i.FilePath, StringComparer.OrdinalIgnoreCase).ToList()
                If files.Count < 2 Then Continue For
                For Each video In files.Where(Function(f) f.MotionKind = MotionKind.LivePhotoVideo)
                    If result.ContainsKey(video) Then Continue For
                    Dim film = video
                    Dim image = files.FirstOrDefault(
                        Function(f) f.MotionKind = MotionKind.LivePhotoImage AndAlso Not result.ContainsKey(f) AndAlso
                                    ArePair(f.FilePath, f.MotionKind, f.MotionId, film.FilePath, film.MotionKind, film.MotionId))
                    If image Is Nothing Then Continue For
                    result(image) = video
                    result(video) = image
                Next
            Next
            Return result
        End Function

        ''' <summary>Art und Kennung einer Datei: aus dem Katalog, wo dessen Zeile zur Datei auf der
        ''' Platte passt, sonst aus der Datei selbst (derselbe Leser wie im EXIF-Lauf, mit dessen
        ''' Zwischenspeicher). Wirft nie.</summary>
        Public Shared Function IdentityOf(path As String) As MotionIdentity
            Dim result As New MotionIdentity With {.Kind = MotionKind.None, .Id = ""}
            If String.IsNullOrEmpty(path) Then Return result
            Try
                If LibraryService.Instance.TryGetCurrentMotion(path, result.Kind, result.Id) Then Return result
            Catch ex As Exception
                DiagnosticLogService.LogException("LivePhoto.IdentityOf.Catalog", ex)
            End Try
            Try
                Dim data = ExifService.ReadExif(path)
                result.Kind = data.MotionKind
                result.Id = If(data.MotionId, "")
            Catch ex As Exception
                DiagnosticLogService.LogException("LivePhoto.IdentityOf", ex)
                result.Kind = MotionKind.None
                result.Id = ""
            End Try
            Return result
        End Function

        ''' <summary>Fuer die Pruefungen: zaehlt, wie oft die Suche nach der anderen Haelfte auf dem
        ''' Anzeigefaden lief. Sie liest Ordner und im Zweifel beide Dateien und gehoert in den
        ''' Hintergrund.</summary>
        Friend Shared PartnerLookupsOnUiThread As Integer

        ''' <summary>Die andere Haelfte eines Live Photos zu diesem Pfad, oder Nothing. Erst der
        ''' Ordner: liegt dort keine Datei gleichen Stamms mit der passenden Endung, ist die Frage
        ''' schon beantwortet, ohne dass eine Datei geoeffnet wird. Liest Ordner und im Zweifel
        ''' Dateien: nicht fuer jede Kachel auf dem Anzeigefaden rufen. Wirft nie.</summary>
        Public Shared Function FindPartnerPath(path As String) As String
            If String.IsNullOrEmpty(path) Then Return Nothing
            Try
                If Avalonia.Threading.Dispatcher.UIThread.CheckAccess() Then Threading.Interlocked.Increment(PartnerLookupsOnUiThread)
            Catch
            End Try
            Dim wantsVideo = IsImageHalf(path)
            If Not wantsVideo AndAlso Not IsVideoHalf(path) Then Return Nothing
            Try
                Dim folder = IO.Path.GetDirectoryName(path)
                Dim stem = IO.Path.GetFileNameWithoutExtension(path)
                If String.IsNullOrEmpty(folder) OrElse String.IsNullOrEmpty(stem) OrElse Not IO.Directory.Exists(folder) Then Return Nothing
                Dim candidates = IO.Directory.EnumerateFiles(folder, stem & ".*").
                    Where(Function(c) Not String.Equals(c, path, PathIdentity.Comparison) AndAlso
                                      String.Equals(IO.Path.GetFileNameWithoutExtension(c), stem, StringComparison.OrdinalIgnoreCase) AndAlso
                                      If(wantsVideo, IsVideoHalf(c), IsImageHalf(c))).
                    OrderBy(Function(c) c, StringComparer.OrdinalIgnoreCase).
                    ToList()
                If candidates.Count = 0 Then Return Nothing
                Dim own = IdentityOf(path)
                If String.IsNullOrEmpty(own.Id) Then Return Nothing
                For Each candidate In candidates
                    Dim other = IdentityOf(candidate)
                    If ArePair(path, own.Kind, own.Id, candidate, other.Kind, other.Id) Then Return candidate
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("LivePhoto.FindPartnerPath", ex)
            End Try
            Return Nothing
        End Function

        ''' <summary>Die Pfade samt den anderen Haelften ihrer Live Photos, jede Datei einmal, die
        ''' Haelfte gleich hinter ihrer Datei. Fuer Loeschen, Verschieben und Kopieren ausserhalb der
        ''' Galerie. Liest Ordner und Dateien: im Hintergrund rufen.</summary>
        Public Shared Function WithPartners(paths As IEnumerable(Of String)) As List(Of String)
            Dim result As New List(Of String)()
            If paths Is Nothing Then Return result
            Dim seen As New HashSet(Of String)(PathIdentity.Comparer)
            For Each path In paths
                If String.IsNullOrEmpty(path) Then Continue For
                If seen.Add(path) Then result.Add(path)
                Dim partner = FindPartnerPath(path)
                If partner IsNot Nothing AndAlso seen.Add(partner) Then result.Add(partner)
            Next
            Return result
        End Function

        ''' <summary>Der Name, den die andere Haelfte bekommt, wenn ihre Datei umbenannt oder
        ''' verschoben wird: Ordner und Stamm des neuen Pfads, die eigene Endung bleibt.</summary>
        Public Shared Function PartnerTargetFor(partnerPath As String, newPath As String) As String
            Dim folder = If(IO.Path.GetDirectoryName(newPath), "")
            Return IO.Path.Combine(folder, IO.Path.GetFileNameWithoutExtension(newPath) & IO.Path.GetExtension(partnerPath))
        End Function

    End Class

End Namespace
