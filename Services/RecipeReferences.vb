Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Threading

Namespace Services

    ''' <summary>
    ''' Die Dateien, die ein Rezept ausser dem Bild selbst liest, und die Grenze dafuer beim Rechnen
    ''' fuer einen anderen als den Eigentuemer.
    '''
    ''' <para>WARUM. Ein Rezept (.fpxmp neben dem Bild, recipe.json in einem .fpx) nennt Dateien mit
    ''' ihrem Pfad: das Bild eines Objekts (ImagePath, auch SVG) und die LUT (LutPath). Rechnet die
    ''' Seite im Browser das Bild fuer einen Benutzer mit Regeln je Ordner, oder eine Freigabe fuer
    ''' einen Gast, kaeme ein Bild aus einem Ordner, den dieser Benutzer nicht sehen darf, sonst
    ''' eingerechnet mit heraus. Beistelldateien nehmen deshalb nur Administratoren an
    ''' (BrowserBridge.CarriesRecipe); diese Grenze ist die zweite Linie dahinter.</para>
    '''
    ''' <para>WIE. Wer fuer einen solchen Kontext rechnet, setzt mit <see cref="Restrict"/> fuer die
    ''' Dauer des Rechnens die Leseregel (MobileContext.CanDownload des Anfragenden bzw. des
    ''' Teilenden). Die Stellen der Kette, die eine solche Datei oeffnen (DrawImageAnnotation,
    ''' DrawSvgAnnotation, die Miniatur einer Bildebene, die LUT), fragen <see cref="Allows"/> und
    ''' lassen die Datei weg, wenn die Regel sie nicht freigibt. Ohne gesetzte Regel (Desktop,
    ''' Administrator) gilt alles wie bisher.</para>
    '''
    ''' <para>Die Regel wandert mit dem Ausfuehrungskontext (AsyncLocal), also auch in die parallelen
    ''' Schleifen der Kette. Gesetzt wird sie IN der Arbeit, die rechnet (im Auftrag der Schlange,
    ''' unter DecodeGate.Run), nicht im Handler davor: die Schlange der mobilen Seite rechnet auf
    ''' einem eigenen Arbeiter, in den der Kontext des Handlers nicht mitgeht.</para>
    '''
    ''' <para>ZWISCHENSPEICHER. Kacheln im Kachelspeicher und die Vorschauen der Seite gelten fuer
    ''' alle. Wo ein Rezept eine Datei nennt, die der Kontext nicht lesen darf
    ''' (<see cref="AllReadable"/> falsch), rechnet die Seite ohne Speicher und legt nichts ab; sonst
    ''' bliebe entweder das fremde Bild in der Kachel fuer diesen Benutzer, oder die gekuerzte Kachel
    ''' fuer alle anderen.</para>
    '''
    ''' <para>Zugelassen sind immer: Symbole der Anwendung (avares://), was aus einem .fpx-Buendel
    ''' entpackt wurde (FpxService.ExtractionRoot) und die LUTs, die ein Administrator in den
    ''' Einstellungen eingetragen hat.</para>
    ''' </summary>
    Friend NotInheritable Class RecipeReferences

        Private Sub New()
        End Sub

        Private Shared ReadOnly Reader As New AsyncLocal(Of Func(Of String, Boolean))()

        ''' <summary>Je Bild die genannten Dateien, solange sich das Rezept nicht aendert.</summary>
        Private Shared ReadOnly Known As New ConcurrentDictionary(Of String, (Stamp As String, Files As String()))(StringComparer.Ordinal)

        Private Const MaxKnown As Integer = 4096

        ''' <summary>Setzt die Leseregel bis zum Dispose. <paramref name="canRead"/> Nothing heisst:
        ''' keine Grenze (Administrator), und es aendert sich nichts.</summary>
        Friend Shared Function Restrict(canRead As Func(Of String, Boolean)) As IDisposable
            If canRead Is Nothing Then Return New Scope(Nothing, False)
            Dim previous = Reader.Value
            Reader.Value = canRead
            Return New Scope(previous, True)
        End Function

        ''' <summary>Gilt gerade eine Grenze?</summary>
        Friend Shared ReadOnly Property IsRestricted As Boolean
            Get
                Return Reader.Value IsNot Nothing
            End Get
        End Property

        ''' <summary>Darf die Kette diese Datei unter der gerade gesetzten Regel lesen? Ohne Regel
        ''' immer.</summary>
        Friend Shared Function Allows(path As String) As Boolean
            Return AllowsUnder(path, Reader.Value)
        End Function

        ''' <summary>Darf <paramref name="canRead"/> jede Datei lesen, die das Rezept dieses Bildes
        ''' (Datei oder Schluessel einer Variante) nennt? Mit <paramref name="canRead"/> Nothing
        ''' immer. Falsch heisst: nicht aus einem gemeinsamen Zwischenspeicher liefern, sondern unter
        ''' <see cref="Restrict"/> neu rechnen.</summary>
        Friend Shared Function AllReadable(key As String, canRead As Func(Of String, Boolean)) As Boolean
            If canRead Is Nothing Then Return True
            For Each file In ReferencedFiles(key)
                If Not AllowsUnder(file, canRead) Then Return False
            Next
            Return True
        End Function

        ''' <summary>Die Dateien, die das Rezept eines Bildes nennt: Objektbilder und SVG mit
        ''' eigenem Pfad und die LUT. Was im Bündel selbst liegt und Symbole der Anwendung zaehlen
        ''' nicht. Leer ohne Rezept.</summary>
        Friend Shared Function ReferencedFiles(key As String) As IReadOnlyList(Of String)
            If String.IsNullOrWhiteSpace(key) Then Return Array.Empty(Of String)()
            Dim file = VariantKey.FileOf(key)
            Dim isFpx = FpxService.IsFpx(file)
            Dim recipeFile As String
            If isFpx Then
                recipeFile = file
            ElseIf RawSidecarService.HasRecipeBeside(file) Then
                recipeFile = RawSidecarService.SidecarPathFor(file)
            Else
                Return Array.Empty(Of String)()
            End If
            Dim stamp As String
            Try
                Dim info As New FileInfo(recipeFile)
                If Not info.Exists Then Return Array.Empty(Of String)()
                stamp = info.LastWriteTimeUtc.Ticks.ToString(Globalization.CultureInfo.InvariantCulture) & "|" &
                        info.Length.ToString(Globalization.CultureInfo.InvariantCulture)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Array.Empty(Of String)()
            End Try
            Dim cached As (Stamp As String, Files As String()) = Nothing
            If Known.TryGetValue(key, cached) AndAlso cached.Stamp = stamp Then Return cached.Files

            Dim adjustments As ImageAdjustments = Nothing
            Try
                adjustments = If(isFpx, FpxService.ReadRecipeOnly(file), RawSidecarService.TryRead(key))
            Catch ex As Exception When TypeOf ex IsNot OutOfMemoryException
                DiagnosticLogService.LogException("RecipeReferences.Read", ex)
            End Try
            Dim files = Collect(adjustments, isFpx)
            If Known.Count >= MaxKnown Then Known.Clear()
            Known(key) = (stamp, files)
            Return files
        End Function

        Private Shared Function Collect(adjustments As ImageAdjustments, isFpx As Boolean) As String()
            If adjustments Is Nothing Then Return Array.Empty(Of String)()
            Dim result As New List(Of String)()
            If Not String.IsNullOrWhiteSpace(adjustments.LutPath) Then result.Add(adjustments.LutPath.Trim())
            If adjustments.Annotations IsNot Nothing Then
                For Each annotation In adjustments.Annotations
                    Dim source = annotation?.ImagePath
                    If String.IsNullOrWhiteSpace(source) OrElse IsAppAsset(source) Then Continue For
                    If isFpx AndAlso FpxService.IsBundleAsset(source) Then Continue For
                    result.Add(source)
                Next
            End If
            Return result.Distinct(StringComparer.Ordinal).ToArray()
        End Function

        Private Shared Function AllowsUnder(path As String, canRead As Func(Of String, Boolean)) As Boolean
            If canRead Is Nothing Then Return True
            If String.IsNullOrWhiteSpace(path) Then Return True
            If IsAppAsset(path) Then Return True
            Dim full As String
            Try
                full = IO.Path.GetFullPath(path.Trim())
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException OrElse
                                       TypeOf ex Is PathTooLongException OrElse TypeOf ex Is Security.SecurityException
                Return False
            End Try
            If IsInsideBundleExtraction(full) OrElse IsRegisteredLut(full) Then Return True
            Try
                Return canRead(full)
            Catch ex As Exception When TypeOf ex IsNot OutOfMemoryException
                Return False
            End Try
        End Function

        Private Shared Function IsAppAsset(path As String) As Boolean
            Return path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function IsInsideBundleExtraction(full As String) As Boolean
            Dim root = IO.Path.GetFullPath(FpxService.ExtractionRoot)
            If Not root.EndsWith(IO.Path.DirectorySeparatorChar) Then root &= IO.Path.DirectorySeparatorChar
            ' Ein ".." ist nach GetFullPath aufgeloest; ein Verweis in einem entpackten Ordner kann
            ' nicht entstehen (SafeExtractPath, ExtractToFile schreibt Dateien).
            Return full.StartsWith(root, StringComparison.Ordinal)
        End Function

        Private Shared Function IsRegisteredLut(full As String) As Boolean
            Dim own = AppSettingsService.Load()?.LutPresets
            Return own IsNot Nothing AndAlso own.Any(Function(p) p IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(p.Path) AndAlso
                                                                 PathIdentity.AreSame(p.Path, full))
        End Function

        Private NotInheritable Class Scope
            Implements IDisposable

            Private ReadOnly _previous As Func(Of String, Boolean)
            Private ReadOnly _active As Boolean
            Private _done As Boolean

            Public Sub New(previous As Func(Of String, Boolean), active As Boolean)
                _previous = previous
                _active = active
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                If _done OrElse Not _active Then Return
                _done = True
                Reader.Value = _previous
            End Sub
        End Class

    End Class

End Namespace
