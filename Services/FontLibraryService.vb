Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ''' <summary>EIGENE SCHRIFTEN AUS DEM ORDNER "Fonts" DES DATENORDNERS.
    '''
    ''' Auf einem schmalen System, etwa im Behaelter auf einer NAS, bringt das System kaum Schriften
    ''' mit. Was der Nutzer in <see cref="AppFolders.FontsFolder"/> legt (.ttf, .otf, .ttc, .otc,
    ''' auch in Unterordnern), steht im Editor neben den Systemschriften zur Wahl und wird ueberall
    ''' gezeichnet, wo Text ins Bild kommt: Editor, Export, Druck, PSD. Der Ordner ist nicht waehlbar;
    ''' er zieht mit dem Datenordner um.
    '''
    ''' Gelesen wird einmal beim Start im Hintergrund (<see cref="StartLoading"/>), danach nur auf
    ''' "Neu einlesen" in den Einstellungen (<see cref="ReloadAsync"/>) oder wenn der Ordner selbst
    ''' ein anderer ist (anderer Datenordner). Wer zeichnet, ruft vorher <see cref="EnsureLoaded"/>:
    ''' das wartet den ersten Lauf ab, damit ein frueh geoeffnetes Bild nicht mit der Ersatzschrift
    ''' des Systems erscheint. NIE unter einem eigenen Schloss aufrufen: der Lauf leert am Ende die
    ''' Zwischenspeicher des Zeichnens, die ihr eigenes Schloss haben.
    '''
    ''' Gleicher Familienname wie eine Systemschrift: die eigene geht vor, bewusst, damit eine
    ''' bestimmte Fassung genutzt werden kann; das Protokoll vermerkt es.</summary>
    Public NotInheritable Class FontLibraryService

        Private Sub New()
        End Sub

        ''' <summary>Ein Schnitt aus einer Datei. Eine Sammlung (.ttc) liefert mehrere, je Index.</summary>
        Friend NotInheritable Class FontFace
            Friend FilePath As String = ""
            Friend FaceIndex As Integer
            Friend Typeface As SKTypeface
            Friend Family As String = ""
            Friend PostScriptName As String = ""
            Friend Weight As Integer = 400
            Friend Width As Integer = 5
            Friend Italic As Boolean
        End Class

        ''' <summary>Ein eingelesener Stand. Wird als Ganzes ersetzt, nie veraendert; wer ihn
        ''' einmal gegriffen hat, liest ohne Schloss weiter.</summary>
        Private NotInheritable Class LibrarySnapshot
            Friend Folder As String = ""
            Friend Version As Integer
            Friend Faces As New List(Of FontFace)()
            Friend ByFamily As New Dictionary(Of String, List(Of FontFace))(StringComparer.OrdinalIgnoreCase)
            Friend Families As IReadOnlyList(Of String) = Array.Empty(Of String)()
            Friend SourceByHandle As New Dictionary(Of IntPtr, String)()
            ''' <summary>Der Name der Avalonia-Sammlung zu diesem Stand, "" ohne Oberflaeche.</summary>
            Friend AvaloniaCollectionKey As String = ""
        End Class

        ''' <summary>Die Endungen, die gelesen werden. Sammlungen tragen mehrere Schnitte.</summary>
        Private Shared ReadOnly FontExtensions As String() = {".ttf", ".otf", ".ttc", ".otc"}
        Private Shared ReadOnly CollectionExtensions As String() = {".ttc", ".otc"}
        ''' <summary>Mehr Schnitte hat keine uebliche Sammlung; die Grenze haelt eine kaputte
        ''' Datei davon ab, die Schleife endlos laufen zu lassen.</summary>
        Private Const MaxFacesPerCollection As Integer = 64

        Private Shared _snapshot As LibrarySnapshot
        Private Shared _folderOverride As String
        Private Shared _loadTask As Task
        Private Shared _version As Integer
        Private Shared ReadOnly _loadGate As New Object()
        Private Shared ReadOnly _reloadGate As New Object()
        ''' <summary>Kaputte Dateien und verdeckte Systemschriften stehen einmal je Lauf des
        ''' Programms im Protokoll, nicht bei jedem Neu-Einlesen wieder.</summary>
        Private Shared ReadOnly _reported As New HashSet(Of String)(StringComparer.Ordinal)

        ''' <summary>Nach jedem Einlesen, auf dem Faden des Einlesens. Wer die Oberflaeche
        ''' anfasst, reicht an den Anzeigefaden weiter.</summary>
        Public Shared Event Changed As EventHandler

        ''' <summary>Der Ordner, aus dem gelesen wird.</summary>
        Public Shared ReadOnly Property Folder As String
            Get
                Dim forced = Volatile.Read(_folderOverride)
                Return If(String.IsNullOrEmpty(forced), AppFolders.FontsFolder, forced)
            End Get
        End Property

        ''' <summary>Nur fuer den Pruefstand: liest aus einem anderen Ordner, ohne den Datenordner
        ''' umzustellen. Nothing oder "" kehrt zum Ordner "Fonts" des Datenordners zurueck. Liest
        ''' sofort neu.</summary>
        Friend Shared Sub UseFolder(folderPath As String)
            Volatile.Write(_folderOverride, If(folderPath, ""))
            Reload()
        End Sub

        ''' <summary>Beginnt das erste Einlesen im Hintergrund. Mehrfache Aufrufe sind harmlos.</summary>
        Public Shared Sub StartLoading()
            SyncLock _loadGate
                If _loadTask IsNot Nothing OrElse _snapshot IsNot Nothing Then Return
                _loadTask = Task.Run(Sub() Reload())
            End SyncLock
        End Sub

        ''' <summary>Liest neu ein, im Hintergrund. Fuer den Knopf "Neu einlesen".</summary>
        Public Shared Function ReloadAsync() As Task
            Dim run = Task.Run(Sub() Reload())
            SyncLock _loadGate
                _loadTask = run
            End SyncLock
            Return run
        End Function

        ''' <summary>Stellt sicher, dass ein Stand fuer den aktuellen Ordner da ist: wartet ein
        ''' laufendes Einlesen ab, liest beim ersten Mal oder nach einem Wechsel des Ordners
        ''' selbst. Nicht unter einem Schloss aufrufen (siehe oben).</summary>
        Public Shared Sub EnsureLoaded()
            Dim running As Task
            SyncLock _loadGate
                running = _loadTask
            End SyncLock
            If running IsNot Nothing AndAlso Not running.IsCompleted Then
                Try
                    running.Wait()
                Catch ex As Exception
                    DiagnosticLogService.LogException("FontLibrary.Wait", ex)
                End Try
            End If
            Dim current = Volatile.Read(_snapshot)
            If current Is Nothing OrElse Not String.Equals(current.Folder, Folder, StringComparison.Ordinal) Then Reload()
        End Sub

        ''' <summary>Die eigenen Familien, sortiert.</summary>
        Public Shared ReadOnly Property FamilyNames As IReadOnlyList(Of String)
            Get
                EnsureLoaded()
                Return Volatile.Read(_snapshot).Families
            End Get
        End Property

        ''' <summary>Die eigenen Familien des Stands, der gerade da ist, ohne zu warten: fuer die
        ''' Liste zur Wahl, die auf dem Anzeigefaden entsteht. Fehlt ein Stand fuer den aktuellen
        ''' Ordner, ist sie leer, und das Einlesen beginnt im Hintergrund; <see cref="Changed"/>
        ''' meldet danach den neuen Stand, und die Liste baut sich neu. Wer zeichnet, nimmt weiter
        ''' <see cref="EnsureLoaded"/>, damit die richtige Schrift kommt.</summary>
        Public Shared ReadOnly Property LoadedFamilyNames As IReadOnlyList(Of String)
            Get
                Dim current = Volatile.Read(_snapshot)
                If current Is Nothing OrElse Not String.Equals(current.Folder, Folder, StringComparison.Ordinal) Then
                    LoadInBackground()
                    Return Array.Empty(Of String)()
                End If
                Return current.Families
            End Get
        End Property

        ''' <summary>Beginnt ein Einlesen im Hintergrund, wenn keines laeuft.</summary>
        Private Shared Sub LoadInBackground()
            SyncLock _loadGate
                If _loadTask IsNot Nothing AndAlso Not _loadTask.IsCompleted Then Return
                _loadTask = Task.Run(Sub() Reload())
            End SyncLock
        End Sub

        ''' <summary>Wie viele Familien gefunden wurden, ohne auf ein laufendes Einlesen zu
        ''' warten (fuer die Anzeige in den Einstellungen); -1, solange noch nichts gelesen ist.</summary>
        Public Shared ReadOnly Property LoadedFamilyCount As Integer
            Get
                Dim current = Volatile.Read(_snapshot)
                Return If(current Is Nothing, -1, current.Families.Count)
            End Get
        End Property

        ''' <summary>Der Stand des Einlesens; aendert sich mit jedem Lauf. Gehoert in die Schluessel
        ''' der Zwischenspeicher, die nach Familienname Schnitte merken.</summary>
        Friend Shared ReadOnly Property Version As Integer
            Get
                Dim current = Volatile.Read(_snapshot)
                Return If(current Is Nothing, 0, current.Version)
            End Get
        End Property

        ''' <summary>Ob die Familie aus dem eigenen Ordner kommt.</summary>
        Public Shared Function Contains(familyName As String) As Boolean
            If String.IsNullOrWhiteSpace(familyName) Then Return False
            Dim current = Volatile.Read(_snapshot)
            Return current IsNot Nothing AndAlso current.ByFamily.ContainsKey(familyName.Trim())
        End Function

        ''' <summary>Der naechste Schnitt der eigenen Familie zu Fett und Kursiv, oder Nothing, wenn
        ''' die Familie nicht im Ordner liegt. Wie bei den Systemschriften: fehlt ein echter Fett-
        ''' oder Kursivschnitt, kommt der naechste vorhandene, nichts wird kuenstlich verformt.
        ''' Wartet nicht; vorher <see cref="EnsureLoaded"/>.</summary>
        Friend Shared Function Match(familyName As String, bold As Boolean, italic As Boolean) As SKTypeface
            If String.IsNullOrWhiteSpace(familyName) Then Return Nothing
            Dim current = Volatile.Read(_snapshot)
            If current Is Nothing Then Return Nothing
            Dim faces As List(Of FontFace) = Nothing
            If Not current.ByFamily.TryGetValue(familyName.Trim(), faces) Then Return Nothing
            Return Nearest(faces, bold, italic)?.Typeface
        End Function

        ''' <summary>Ein eigener Schnitt, der das Zeichen fuehrt, bevorzugt in Fett und Kursiv wie
        ''' gewuenscht; Nothing, wenn keiner es hat (dann fragt der Aufrufer das System).
        ''' Wartet nicht; vorher <see cref="EnsureLoaded"/>.</summary>
        Friend Shared Function MatchCharacter(codePoint As Integer, bold As Boolean, italic As Boolean) As SKTypeface
            Dim current = Volatile.Read(_snapshot)
            If current Is Nothing OrElse current.Faces.Count = 0 Then Return Nothing
            Dim carrying = current.Faces.Where(Function(f) f.Typeface IsNot Nothing AndAlso f.Typeface.ContainsGlyph(codePoint)).ToList()
            If carrying.Count = 0 Then Return Nothing
            Return Nearest(carrying, bold, italic)?.Typeface
        End Function

        ''' <summary>Die eigene Familie zum PostScript-Namen eines Schnitts (aus einer PSD), oder
        ''' Nothing. Wartet den ersten Lauf ab.</summary>
        Public Shared Function FamilyForPostScriptName(postScriptName As String) As String
            If String.IsNullOrWhiteSpace(postScriptName) Then Return Nothing
            EnsureLoaded()
            Dim name = postScriptName.Trim()
            Dim face = Volatile.Read(_snapshot).Faces.FirstOrDefault(Function(f) String.Equals(f.PostScriptName, name, StringComparison.OrdinalIgnoreCase))
            Return face?.Family
        End Function

        ''' <summary>Woher ein Schnitt kommt ("Datei#Index"), wenn er aus dem eigenen Ordner stammt,
        ''' sonst "". Die Textformung merkt sich ihre Schriften danach: ein eigener Schnitt mit
        ''' denselben Namen wie eine Systemschrift bekommt so seine eigenen Daten.</summary>
        Friend Shared Function SourceOf(typeface As SKTypeface) As String
            If typeface Is Nothing Then Return ""
            Dim current = Volatile.Read(_snapshot)
            If current Is Nothing Then Return ""
            Dim source As String = Nothing
            Return If(current.SourceByHandle.TryGetValue(typeface.Handle, source), source, "")
        End Function

        ''' <summary>Der Familienname fuer die Oberflaeche (Avalonia, etwa die Textbox des Editors):
        ''' fuer eine eigene Familie mit der Sammlung davor ("fonts:...#Name"), sonst der Name selbst.</summary>
        Public Shared Function UiFamilyName(familyName As String) As String
            If String.IsNullOrWhiteSpace(familyName) Then Return If(familyName, "")
            Dim current = Volatile.Read(_snapshot)
            If current Is Nothing OrElse current.AvaloniaCollectionKey.Length = 0 OrElse
               Not current.ByFamily.ContainsKey(familyName.Trim()) Then Return familyName
            Return current.AvaloniaCollectionKey & "#" & familyName.Trim()
        End Function

        Private Shared Function Nearest(faces As IEnumerable(Of FontFace), bold As Boolean, italic As Boolean) As FontFace
            Dim wantedWeight = If(bold, 700, 400)
            Return faces.OrderBy(Function(f) If(f.Italic = italic, 0, 10000) + Math.Abs(f.Weight - wantedWeight) * 4 +
                                             If(f.Weight > wantedWeight AndAlso Not bold, 1, 0) + Math.Abs(f.Width - 5) * 50).
                         ThenBy(Function(f) f.FilePath, StringComparer.Ordinal).
                         ThenBy(Function(f) f.FaceIndex).
                         FirstOrDefault()
        End Function

        ''' <summary>Liest den Ordner und ersetzt den Stand. Kaputte Dateien fallen weg.</summary>
        Private Shared Sub Reload()
            ' Ein Lauf zur Zeit: Start im Hintergrund, Knopf und Ordnerwechsel koennen sich treffen.
            SyncLock _reloadGate
                ReloadLocked()
            End SyncLock
        End Sub

        Private Shared Sub ReloadLocked()
            Dim folderPath = Folder
            Dim built As New LibrarySnapshot With {.Folder = folderPath, .Version = Interlocked.Increment(_version)}
            Try
                If Directory.Exists(folderPath) Then
                    Dim files = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).
                        Where(Function(f) FontExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).
                        OrderBy(Function(f) f, StringComparer.Ordinal).ToList()
                    For Each file In files
                        ReadFile(file, built)
                    Next
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("FontLibrary.Reload", ex)
            End Try
            For Each face In built.Faces
                Dim list As List(Of FontFace) = Nothing
                If Not built.ByFamily.TryGetValue(face.Family, list) Then
                    list = New List(Of FontFace)()
                    built.ByFamily(face.Family) = list
                End If
                list.Add(face)
                built.SourceByHandle(face.Typeface.Handle) = face.FilePath & "#" & face.FaceIndex.ToString(Globalization.CultureInfo.InvariantCulture)
            Next
            built.Families = built.ByFamily.Keys.OrderBy(Function(n) n, StringComparer.OrdinalIgnoreCase).ToList()
            ReportShadowedSystemFamilies(built)
            built.AvaloniaCollectionKey = FontLibraryUiCollection.Register(built.Faces.Select(Function(f) f.FilePath).Distinct(StringComparer.Ordinal), built.Version)
            Volatile.Write(_snapshot, built)
            ImageProcessor.ClearTypefaceCaches()
            DiagnosticLogService.LogAlways("FontLibrary", $"{built.Families.Count} Familien, {built.Faces.Count} Schnitte aus {folderPath}")
            Try
                RaiseEvent Changed(Nothing, EventArgs.Empty)
            Catch ex As Exception
                DiagnosticLogService.LogException("FontLibrary.Changed", ex)
            End Try
        End Sub

        Private Shared Sub ReadFile(filePath As String, target As LibrarySnapshot)
            Dim isCollection = CollectionExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())
            Dim limit = If(isCollection, MaxFacesPerCollection, 1)
            Dim found = 0
            For index = 0 To limit - 1
                Dim typeface As SKTypeface = Nothing
                Try
                    typeface = SKTypeface.FromFile(filePath, index)
                Catch ex As Exception
                    typeface = Nothing
                End Try
                If typeface Is Nothing OrElse String.IsNullOrWhiteSpace(typeface.FamilyName) OrElse typeface.GlyphCount <= 0 Then
                    typeface?.Dispose()
                    Exit For
                End If
                target.Faces.Add(New FontFace With {
                    .FilePath = filePath, .FaceIndex = index, .Typeface = typeface,
                    .Family = typeface.FamilyName.Trim(), .PostScriptName = If(typeface.PostScriptName, ""),
                    .Weight = typeface.FontWeight, .Width = typeface.FontWidth,
                    .Italic = typeface.FontSlant <> SKFontStyleSlant.Upright})
                found += 1
            Next
            If found = 0 Then ReportOnce("broken|" & filePath, $"Datei ohne lesbare Schrift uebersprungen: {filePath}")
        End Sub

        Private Shared Sub ReportShadowedSystemFamilies(built As LibrarySnapshot)
            If built.Families.Count = 0 Then Return
            Try
                Dim system = New HashSet(Of String)(SKFontManager.Default.FontFamilies, StringComparer.OrdinalIgnoreCase)
                For Each family In built.Families
                    If system.Contains(family) Then ReportOnce("shadow|" & family, $"Eigene Schrift geht vor der gleichnamigen Systemschrift: {family}")
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("FontLibrary.SystemFamilies", ex)
            End Try
        End Sub

        Private Shared Sub ReportOnce(key As String, message As String)
            SyncLock _reported
                If Not _reported.Add(key) Then Return
            End SyncLock
            DiagnosticLogService.LogAlways("FontLibrary", message)
        End Sub

    End Class

    ''' <summary>Die eigenen Schriften fuer Avalonia, damit die Textbox des Editors (Eingabe und
    ''' Treffer der Schreibmarke) in derselben Schrift setzt wie das Bild. Je Einlesen eine eigene
    ''' Sammlung unter neuem Namen: eine ersetzte Sammlung gibt Avalonia frei, und eine Textbox, die
    ''' gerade noch daraus setzt, griffe dann ins Leere. Die alten bleiben bis zum Ende stehen.
    '''
    ''' Avalonia liest aus einer Sammlungsdatei (.ttc) nur den ersten Schnitt; das Bild selbst
    ''' zeichnet trotzdem jeden.</summary>
    Friend NotInheritable Class FontLibraryUiCollection
        Inherits Avalonia.Media.Fonts.FontCollectionBase

        Private Const KeyPrefix As String = "fonts:FerrumPixFonts"
        Private ReadOnly _key As Uri

        Private Sub New(key As Uri, files As IEnumerable(Of String))
            _key = key
            For Each file In files
                Try
                    Using stream = IO.File.OpenRead(file)
                        Dim unused As Avalonia.Media.GlyphTypeface = Nothing
                        TryAddGlyphTypeface(stream, unused)
                    End Using
                Catch ex As Exception
                    ' Kaputte Dateien meldet schon das Einlesen fuer das Bild.
                End Try
            Next
        End Sub

        Public Overrides ReadOnly Property Key As Uri
            Get
                Return _key
            End Get
        End Property

        ''' <summary>Meldet die Sammlung an und gibt ihren Namen zurueck; "" ohne Dateien oder
        ''' ohne Oberflaeche (Pruefstand ohne Avalonia).</summary>
        Friend Shared Function Register(files As IEnumerable(Of String), version As Integer) As String
            Dim list = files.ToList()
            If list.Count = 0 Then Return ""
            Try
                Dim manager = Avalonia.Media.FontManager.Current
                If manager Is Nothing Then Return ""
                Dim key = New Uri(KeyPrefix & version.ToString(Globalization.CultureInfo.InvariantCulture), UriKind.Absolute)
                manager.AddFontCollection(New FontLibraryUiCollection(key, list))
                Return key.ToString()
            Catch ex As Exception
                Return ""
            End Try
        End Function

    End Class

End Namespace
