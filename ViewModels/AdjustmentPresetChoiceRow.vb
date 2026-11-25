Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Eine Zeile in einer Auswahlliste gespeicherter Anpassungen, die die ORDNER ALS
    ''' HIERARCHIE zeigt statt "Ordner / Name" in einer Zeile: ein Ordner ist eine Ueberschrift,
    ''' die sich nicht waehlen laesst, darunter stehen eingerueckt seine Anpassungen.
    '''
    ''' DIE REIHENFOLGE (<see cref="BuildTree"/>): zuerst die Anpassungen des Hauptordners ohne
    ''' Ueberschrift, dann die Ordner in der Reihenfolge des Baums, jeder vor seinen Unterordnern.
    ''' Ein Ordner erscheint nur, wenn in ihm oder unter ihm eine Anpassung liegt; ein leerer
    ''' fehlt. Die Einrueckung folgt der Tiefe: eine Ueberschrift steht so tief wie ihr Ordner,
    ''' ihre Anpassungen eine Stufe tiefer.
    '''
    ''' UEBERSCHRIFTEN WERDEN NIE GEWAEHLT. Landet die Liste auf einer (PFEIL HOCH und RUNTER,
    ''' ein getippter Buchstabe), nennt <see cref="Resolve"/> die naechste waehlbare Zeile in
    ''' der Richtung des Wegs. Der Text einer Ueberschrift ist fuer die Suche nach einem
    ''' Anfangsbuchstaben leer (<see cref="ToString"/>).
    '''
    ''' Dieselbe Zeile dient der Liste im Editor (mit <see cref="Preset"/>) und der Vorgabenliste
    ''' im Stapeldialog (mit <see cref="Key"/>, dem Eintrag, unter dem der Dialog die Vorgabe
    ''' fuehrt); dort tragen Filter, Presets und LUTs flache Zeilen ohne Ueberschrift
    ''' (<see cref="BuildFlat"/>).</summary>
    Public NotInheritable Class AdjustmentPresetChoiceRow

        ''' Um so viele Punkte rueckt jede Stufe ein.
        Public Const IndentStep As Double = 14

        ''' Eine Ordner-Ueberschrift: nicht waehlbar.
        Public ReadOnly Property IsHeader As Boolean
        ''' Der Name des Ordners oder der Anpassung, ohne Pfad.
        Public ReadOnly Property Text As String
        ''' Was das geschlossene Feld zeigt: der Name mit Ordnerpfad davor.
        Public ReadOnly Property ClosedText As String
        ''' Die Stufe der Einrueckung; 0 steht am Rand.
        Public ReadOnly Property Depth As Integer
        Public ReadOnly Property Indent As Avalonia.Thickness
        ''' Die Anpassung der Zeile; Nothing bei einer Ueberschrift und bei flachen Zeilen.
        Public ReadOnly Property Preset As AdjustmentPresetFile
        ''' Der Eintrag, den die Zeile vertritt (Stapeldialog); bei einer Ueberschrift der Ordnerpfad.
        Public ReadOnly Property Key As String

        Private Sub New(isHeader As Boolean, text As String, closedText As String, depth As Integer,
                        preset As AdjustmentPresetFile, key As String)
            Me.IsHeader = isHeader
            Me.Text = If(text, "")
            Me.ClosedText = If(closedText, "")
            Me.Depth = Math.Max(0, depth)
            Indent = New Avalonia.Thickness(Me.Depth * IndentStep, 0, 0, 0)
            Me.Preset = preset
            Me.Key = If(key, "")
        End Sub

        Public ReadOnly Property IsSelectable As Boolean
            Get
                Return Not IsHeader
            End Get
        End Property

        ''' <summary>Der Text fuer die Suche nach einem getippten Anfangsbuchstaben: der Name der
        ''' Anpassung, bei einer Ueberschrift nichts.</summary>
        Public Overrides Function ToString() As String
            Return If(IsHeader, "", Text)
        End Function

        ''' <summary>Die Zeilen zu einem Bestand: Hauptordner zuerst ohne Ueberschrift, dann jeder
        ''' Ordner mit Anpassungen (in ihm oder unter ihm) als Ueberschrift vor seinen Anpassungen
        ''' und Unterordnern. Anpassungen eines Ordners stehen nach Namen.</summary>
        ''' <param name="keyOf">Der Eintrag je Anpassung (Stapeldialog); ohne ihn die Kennung.</param>
        Public Shared Function BuildTree(presets As IEnumerable(Of AdjustmentPresetFile),
                                         Optional keyOf As Func(Of AdjustmentPresetFile, String) = Nothing) As List(Of AdjustmentPresetChoiceRow)
            Dim result As New List(Of AdjustmentPresetChoiceRow)()
            Dim all = If(presets, Enumerable.Empty(Of AdjustmentPresetFile)()).Where(Function(p) p IsNot Nothing).ToList()
            ' Jeder Ordner mit Anpassungen und alle seine Vorfahren: ein Ordner, der nur
            ' Unterordner mit Anpassungen hat, traegt die Hierarchie und erscheint deshalb auch.
            Dim folders As New HashSet(Of String)(StringComparer.Ordinal)
            ' NICHT "preset" als Laufvariable: VB bindet den Namen an die Eigenschaft Preset.
            For Each entry In all
                Dim folder = AdjustmentPresetStore.NormalizeFolder(entry.Folder)
                While folder.Length > 0 AndAlso folders.Add(folder)
                    folder = AdjustmentPresetStore.ParentFolder(folder)
                End While
            Next
            AppendPresets(result, all, "", 0, keyOf)
            For Each folder In AdjustmentPresetStore.OrderFoldersAsTree(folders)
                Dim depth = folder.Split("/"c).Length - 1
                result.Add(New AdjustmentPresetChoiceRow(True, AdjustmentPresetStore.FolderName(folder),
                                                         folder.Replace("/", " / "), depth, Nothing, folder))
                AppendPresets(result, all, folder, depth + 1, keyOf)
            Next
            Return result
        End Function

        Private Shared Sub AppendPresets(result As List(Of AdjustmentPresetChoiceRow), all As List(Of AdjustmentPresetFile),
                                         folder As String, depth As Integer, keyOf As Func(Of AdjustmentPresetFile, String))
            For Each entry In all.Where(Function(p) String.Equals(AdjustmentPresetStore.NormalizeFolder(p.Folder), folder, StringComparison.Ordinal)).
                                  OrderBy(Function(p) p.Name, StringComparer.CurrentCultureIgnoreCase)
                result.Add(New AdjustmentPresetChoiceRow(False, entry.Name, entry.DisplayPath, depth, entry,
                                                         If(keyOf Is Nothing, entry.Id, keyOf(entry))))
            Next
        End Sub

        ''' <summary>Flache Zeilen ohne Ueberschrift, je Eintrag eine: fuer Listen, die keine
        ''' Ordner kennen (Filter, XMP-Presets, LUTs im Stapeldialog).</summary>
        Public Shared Function BuildFlat(labels As IEnumerable(Of String)) As List(Of AdjustmentPresetChoiceRow)
            Return If(labels, Enumerable.Empty(Of String)()).
                Select(Function(label) New AdjustmentPresetChoiceRow(False, label, label, 0, Nothing, label)).ToList()
        End Function

        ''' <summary>Welche Zeile gilt, wenn die Liste <paramref name="requested"/> waehlen will?
        ''' Eine waehlbare gilt selbst. Bei einer Ueberschrift die naechste waehlbare in der
        ''' Richtung, in der die Wahl von <paramref name="current"/> aus gewandert ist; gibt es
        ''' dort keine, die naechste in der anderen Richtung, sonst bleibt es bei
        ''' <paramref name="current"/>.</summary>
        Public Shared Function Resolve(rows As IList(Of AdjustmentPresetChoiceRow), requested As AdjustmentPresetChoiceRow,
                                       current As AdjustmentPresetChoiceRow) As AdjustmentPresetChoiceRow
            If requested Is Nothing OrElse rows Is Nothing Then Return current
            If Not requested.IsHeader Then Return requested
            Dim index = rows.IndexOf(requested)
            If index < 0 Then Return current
            Dim from = If(current Is Nothing, -1, rows.IndexOf(current))
            Dim stepDirection = If(from < 0 OrElse index > from, 1, -1)
            For Each direction In {stepDirection, -stepDirection}
                Dim i = index + direction
                While i >= 0 AndAlso i < rows.Count
                    If Not rows(i).IsHeader Then Return rows(i)
                    i += direction
                End While
            Next
            Return current
        End Function

    End Class

End Namespace
