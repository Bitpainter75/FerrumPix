Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports FerrumPix.Models

Namespace Services

    ''' <summary>Eine Aufnahme: eine Datei, oder ein RAW mit dem JPEG, das die Kamera daneben
    ''' geschrieben hat. Die erste Datei fuehrt (beim Paar das RAW).</summary>
    Public NotInheritable Class StackShot
        Public ReadOnly Property Files As New List(Of ImageItem)()

        Public ReadOnly Property Primary As ImageItem
            Get
                Return Files(0)
            End Get
        End Property

        Public ReadOnly Property TakenAt As DateTime?
            Get
                Return Primary.ExifDateTaken
            End Get
        End Property

        Public ReadOnly Property IsPair As Boolean
            Get
                Return Files.Count > 1
            End Get
        End Property
    End Class

    ''' <summary>Ein Stapel: eine Serie aus mehreren Aufnahmen, ein RAW+JPEG-Paar, oder beides.
    ''' Wird bei jedem Filterlauf neu gebildet; was ueber einen Lauf hinaus gilt (aufgeklappt oder
    ''' nicht), haengt am Schluessel.</summary>
    Public NotInheritable Class ImageStack
        ''' <summary>Pfad der ersten Datei der ersten Aufnahme. Bleibt stehen, solange die Serie
        ''' vorn nicht waechst - genug, um den Aufklappzustand ueber einen Filterlauf zu tragen.</summary>
        Public Property Key As String = ""
        Public ReadOnly Property Shots As New List(Of StackShot)()
        Public Property LeadShot As StackShot

        Public ReadOnly Property IsBurst As Boolean
            Get
                Return Shots.Count > 1
            End Get
        End Property

        Public ReadOnly Property HasPair As Boolean
            Get
                Return Shots.Any(Function(s) s.IsPair)
            End Get
        End Property

        Public ReadOnly Property FileCount As Integer
            Get
                Return Shots.Sum(Function(s) s.Files.Count)
            End Get
        End Property

        Public Iterator Function AllFiles() As IEnumerable(Of ImageItem)
            For Each shot In Shots
                For Each file In shot.Files
                    Yield file
                Next
            Next
        End Function

        Public Function ShotOf(item As ImageItem) As StackShot
            Return Shots.FirstOrDefault(Function(s) s.Files.Contains(item))
        End Function
    End Class

    ''' <summary>Bildet Stapel aus einer Bildliste: zuerst RAW+JPEG-Paare, dann Serien aus
    ''' Aufnahmen, die kurz hintereinander entstanden sind.
    '''
    ''' Gearbeitet wird nur mit dem, was am Element schon steht (Pfad, Aufnahmezeit, Kamera); kein
    ''' Dateizugriff. Fehlt die Aufnahmezeit, bildet die Datei keinen Stapel - sie kommt beim
    ''' Ordnerwechsel oft erst mit dem Hintergrundabgleich, und die Galerie rechnet danach neu.</summary>
    Public NotInheritable Class ImageStackService

        ''' <summary>Hoechster Abstand zweier aufeinanderfolgender Aufnahmen einer Serie. EXIF
        ''' fuehrt die Zeit in ganzen Sekunden; 1 heisst also: dieselbe oder die naechste Sekunde.</summary>
        Public Const BurstGapSeconds As Double = 1.0

        ''' <summary>Hoechster Abstand der Aufnahmezeit zwischen RAW und JPEG eines Paars. Die Kamera
        ''' schreibt beiden dieselbe Zeit; ein von Hand exportiertes JPEG traegt meist dieselbe
        ''' Aufnahmezeit auch, deshalb zaehlt zusaetzlich die Kamera.</summary>
        Public Const PairGapSeconds As Double = 2.0

        Private Shared ReadOnly CompanionExtensions As New HashSet(Of String)(
            {".jpg", ".jpeg", ".heic", ".heif", ".hif"}, StringComparer.OrdinalIgnoreCase)

        Private Sub New()
        End Sub

        ''' <summary>Darf die Datei in einen Stapel? Nur lokale Bilder; Serverelemente haben keinen
        ''' Ordner, in dem ein Nachbar liegen koennte, und Videos sind keine Aufnahmen einer Serie.</summary>
        Public Shared Function IsStackable(item As ImageItem) As Boolean
            Return item IsNot Nothing AndAlso item.IsImage AndAlso Not item.IsRemoteAsset AndAlso
                   Not item.IsTrashed AndAlso Not item.IsVideoFile AndAlso
                   Not String.IsNullOrEmpty(item.FilePath)
        End Function

        Public Shared Function Build(items As IEnumerable(Of ImageItem)) As List(Of ImageStack)
            Dim candidates = items.Where(AddressOf IsStackable).ToList()
            Dim standalone As New HashSet(Of StackShot)()
            Dim shots = BuildShots(candidates, standalone)
            Dim stacks As New List(Of ImageStack)()

            ' Serien je Ordner und Kamera; zwei Gehaeuse, die in derselben Sekunde ausloesen, sind
            ' zwei Serien und keine. OHNE Kamera keine Serie: alle Bilder eines Ordners ohne Angabe
            ' teilten sonst denselben leeren Schluessel, und unabhaengige Bilder (Scans, Exporte,
            ' Bildschirmfotos) verschwaenden hinter einer Kachel, nur weil ihre Zeiten nah liegen.
            ' Auch keine Serie aus einer uebrigen Begleitdatei (RAW+JPG+HEIC): sie traegt denselben
            ' Namen und dieselbe Zeit wie das Paar, ist aber keine zweite Ausloesung. Sie steht
            ' einzeln im Raster.
            Dim timed = shots.Where(Function(s) s.TakenAt.HasValue AndAlso Not standalone.Contains(s) AndAlso
                                                Not String.IsNullOrWhiteSpace(s.Primary.ExifCamera)).
                GroupBy(Function(s) SeriesKey(s.Primary), StringComparer.OrdinalIgnoreCase)
            Dim inBurst As New HashSet(Of StackShot)()
            For Each group In timed
                Dim ordered = group.OrderBy(Function(s) s.TakenAt.Value).
                    ThenBy(Function(s) s.Primary.FileName, StringComparer.OrdinalIgnoreCase).ToList()
                Dim run As New List(Of StackShot)()
                For Each shot In ordered
                    If run.Count > 0 AndAlso
                       ((shot.TakenAt.Value - run(run.Count - 1).TakenAt.Value).TotalSeconds > BurstGapSeconds OrElse
                        BothSingleShots(run(run.Count - 1), shot)) Then
                        If run.Count > 1 Then stacks.Add(NewStack(run)) : inBurst.UnionWith(run)
                        run = New List(Of StackShot)()
                    End If
                    run.Add(shot)
                Next
                If run.Count > 1 Then stacks.Add(NewStack(run)) : inBurst.UnionWith(run)
            Next

            ' Ein Paar ausserhalb einer Serie ist ein Stapel fuer sich.
            For Each shot In shots
                If shot.IsPair AndAlso Not inBurst.Contains(shot) Then stacks.Add(NewStack({shot}))
            Next
            Return stacks
        End Function

        ''' <summary>Sagt die Kamera bei beiden Aufnahmen "Einzelbild", sind sie keine Serie, auch
        ''' wenn sie in derselben Sekunde entstanden: zwei schnelle Einzelausloesungen sind zwei
        ''' Motive, die nicht hinter einer Kachel verschwinden sollen. Fehlt die Angabe bei einer
        ''' der beiden (Telefone, Bearbeitungen), entscheidet wie bisher allein die Zeit.</summary>
        Private Shared Function BothSingleShots(previous As StackShot, current As StackShot) As Boolean
            Return previous.Primary.CaptureMode = CaptureMode.SingleShot AndAlso
                   current.Primary.CaptureMode = CaptureMode.SingleShot
        End Function

        Private Shared Function NewStack(shots As IEnumerable(Of StackShot)) As ImageStack
            Dim stack As New ImageStack()
            stack.Shots.AddRange(shots)
            stack.Key = stack.Shots(0).Primary.FilePath
            stack.LeadShot = stack.Shots(0)
            Return stack
        End Function

        Private Shared Function SeriesKey(item As ImageItem) As String
            Return FolderOf(item.FilePath) & "|" & If(item.ExifCamera, "")
        End Function

        Private Shared Function FolderOf(path As String) As String
            Return If(IO.Path.GetDirectoryName(path), "")
        End Function

        ''' <summary>Fasst RAW und JPEG derselben Aufnahme zusammen: gleicher Ordner, gleicher Name
        ''' ohne Endung, genau ein RAW, Aufnahmezeit bei beiden bekannt und hoechstens
        ''' <see cref="PairGapSeconds"/> auseinander, und, wo beide sie nennen, dieselbe Kamera.
        ''' Was davon abweicht, bleibt eine eigene Aufnahme - lieber ein Paar zu wenig als ein
        ''' fremdes JPEG, das hinter dem RAW verschwindet.</summary>
        Private Shared Function BuildShots(items As List(Of ImageItem), standalone As HashSet(Of StackShot)) As List(Of StackShot)
            Dim result As New List(Of StackShot)()
            Dim byName = items.GroupBy(Function(i) Path.Combine(FolderOf(i.FilePath), Path.GetFileNameWithoutExtension(i.FilePath)),
                                       StringComparer.OrdinalIgnoreCase)
            For Each group In byName
                Dim files = group.ToList()
                Dim raws = files.Where(Function(f) f.IsRawFile).ToList()
                If files.Count < 2 OrElse raws.Count <> 1 Then
                    For Each file In files
                        result.Add(SingleShot(file))
                    Next
                    Continue For
                End If

                ' Genau EINE Begleitdatei: die Kamera schreibt zu einem RAW ein JPEG oder ein HEIF,
                ' nicht beides. Liegen mehrere passende daneben (RAW+JPG+HEIC), ist mindestens eine
                ' davon nicht das Kamerabild - sie bleibt sichtbar als eigene Aufnahme, statt hinter
                ' dem RAW zu verschwinden. JPEG geht vor HEIF, bei Gleichstand die naehere Zeit.
                Dim raw = raws(0)
                Dim shot = SingleShot(raw)
                Dim companion = files.Where(Function(f) f IsNot raw AndAlso IsCompanion(raw, f)).
                    OrderBy(Function(f) CompanionRank(f)).
                    ThenBy(Function(f) Math.Abs((f.ExifDateTaken.Value - raw.ExifDateTaken.Value).TotalSeconds)).
                    ThenBy(Function(f) f.FilePath, StringComparer.OrdinalIgnoreCase).
                    FirstOrDefault()
                If companion IsNot Nothing Then shot.Files.Add(companion)
                For Each file In files
                    If file Is raw OrElse file Is companion Then Continue For
                    Dim leftover = SingleShot(file)
                    standalone.Add(leftover)
                    result.Add(leftover)
                Next
                result.Add(shot)
            Next
            Return result
        End Function

        Private Shared Function SingleShot(item As ImageItem) As StackShot
            Dim shot As New StackShot()
            shot.Files.Add(item)
            Return shot
        End Function

        Private Shared Function CompanionRank(item As ImageItem) As Integer
            Select Case Path.GetExtension(item.FilePath).ToLowerInvariant()
                Case ".jpg", ".jpeg" : Return 0
                Case Else : Return 1
            End Select
        End Function

        Friend Shared Function IsCompanion(raw As ImageItem, other As ImageItem) As Boolean
            If Not CompanionExtensions.Contains(Path.GetExtension(other.FilePath)) Then Return False
            If Not raw.ExifDateTaken.HasValue OrElse Not other.ExifDateTaken.HasValue Then Return False
            If Math.Abs((raw.ExifDateTaken.Value - other.ExifDateTaken.Value).TotalSeconds) > PairGapSeconds Then Return False
            Dim rawCamera = If(raw.ExifCamera, "")
            Dim otherCamera = If(other.ExifCamera, "")
            If rawCamera.Length > 0 AndAlso otherCamera.Length > 0 AndAlso
               Not String.Equals(rawCamera, otherCamera, StringComparison.OrdinalIgnoreCase) Then Return False
            Return True
        End Function

        ''' <summary>Legt die fuehrende Aufnahme fest: die schaerfste, sobald fuer jede Aufnahme der
        ''' Serie ein Wert vorliegt, sonst die erste. Solange nur ein Teil gemessen ist, bleibt die
        ''' erste vorn - sonst sprange die Kachel mit jedem fertigen Messwert.</summary>
        Public Shared Sub ChooseLead(stack As ImageStack, scoreOf As Func(Of ImageItem, Double?))
            If stack Is Nothing OrElse stack.Shots.Count = 0 Then Return
            stack.LeadShot = stack.Shots(0)
            If Not stack.IsBurst Then Return
            Dim scores = stack.Shots.Select(Function(s) scoreOf(s.Primary)).ToList()
            If scores.Any(Function(v) Not v.HasValue) Then Return
            Dim best = 0
            For i = 1 To scores.Count - 1
                If scores(i).Value > scores(best).Value Then best = i
            Next
            stack.LeadShot = stack.Shots(best)
        End Sub
    End Class

End Namespace
