Imports System
Imports System.Threading

Namespace Services

    ''' <summary>
    ''' Die anwendungsweite Auskunft, ob ein Pfad auf einem Laufwerk liegt, das gerade nicht
    ''' erreichbar ist (Wechseldatentraeger abgezogen, Netzfreigabe nicht eingehaengt).
    '''
    ''' WOZU: die Galerie zeigt einen solchen Ordner aus dem Katalog statt von der Platte, und
    ''' jede Dateioperation dort ist aus (KATALOGINDEX.md, "Galerie offline"). Die Entscheidung
    ''' faellt VOR jedem Blick auf die Platte: ein Directory.Exists auf einer haengenden Freigabe
    ''' kann Sekunden dauern, und genau das soll hier nicht passieren.
    '''
    ''' Die eigentliche Erkennung ist <see cref="VolumePresence"/>. Hier liegt nur ein gemeinsamer,
    ''' kurz gueltiger Stand davon, damit Ordnerbaum, Kachelbindungen und Dateirichtlinie nicht je
    ''' Aufruf die Einhaengeorte neu lesen. Wer einen frischen Stand braucht (Ordner neu gewaehlt,
    ''' Aktualisieren), ruft <see cref="Invalidate"/>.
    ''' </summary>
    Public NotInheritable Class OfflineVolumes

        Private Sub New()
        End Sub

        ''' <summary>So lange gilt ein gelesener Stand, wenn ihn niemand vorher verwirft. Kurz
        ''' genug, dass ein wieder eingestecktes Laufwerk ohne eigenes Zutun zurueckkommt, lang
        ''' genug, dass ein Ordnerbaum mit hundert Knoten die Einhaengeorte einmal liest und nicht
        ''' hundertmal.</summary>
        Private Const SnapshotLifetimeMs As Long = 5000

        Private Shared ReadOnly _lock As New Object()
        Private Shared _snapshot As VolumePresence
        Private Shared _snapshotTicks As Long
        Private Shared _testPresence As VolumePresence

        ''' <summary>Der Einhaengeort, wenn der Pfad auf einem nicht erreichbaren Laufwerk liegt,
        ''' sonst "".</summary>
        Public Shared Function UnreachableRootOf(path As String) As String
            If String.IsNullOrWhiteSpace(path) Then Return ""
            ' Serverbilder tragen Pseudopfade (immich://, nextcloud://); sie liegen auf keinem
            ' Laufwerk und sind hier nicht gemeint.
            If path.Contains("://", StringComparison.Ordinal) Then Return ""
            SyncLock _lock
                Return CurrentLocked().UnreachableRootOf(path)
            End SyncLock
        End Function

        ''' <summary>Liegt der Pfad auf einem Laufwerk, das gerade nicht erreichbar ist?</summary>
        Public Shared Function IsUnreachable(path As String) As Boolean
            Return UnreachableRootOf(path).Length > 0
        End Function

        ''' <summary>Wie <see cref="IsUnreachable"/>, fuer einen ORDNER: auch der Einhaengeort selbst
        ''' zaehlt. <see cref="VolumePresence"/> fragt nach dem Ort, auf dem ein Pfad LIEGT; fuer
        ''' <c>/mnt/nas</c> ist das keiner, erst fuer <c>/mnt/nas/bild.jpg</c>. Ein Ordner wird
        ''' deshalb auch ueber einen Pfad darunter gefragt. Fuer Dateien gilt das nicht: eine Datei
        ''' direkt unter <c>/mnt</c> ist kein Einhaengeort.</summary>
        Public Shared Function IsFolderUnreachable(folderPath As String) As Boolean
            If String.IsNullOrWhiteSpace(folderPath) Then Return False
            If IsUnreachable(folderPath) Then Return True
            Dim trimmed = folderPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
            If trimmed.Length = 0 Then Return False
            Return IsUnreachable(trimmed & IO.Path.DirectorySeparatorChar & "x")
        End Function

        ''' <summary>Verwirft den gemerkten Stand; die naechste Frage liest neu.</summary>
        Public Shared Sub Invalidate()
            SyncLock _lock
                _snapshot = Nothing
            End SyncLock
        End Sub

        ''' <summary>NUR fuer den Pruefstand: ein vorgegebener Stand statt der echten
        ''' Einhaengeorte. Nothing stellt zurueck. Die Anwendung setzt ihn nirgends.</summary>
        Friend Shared Sub UseForTest(presence As VolumePresence)
            SyncLock _lock
                _testPresence = presence
                _snapshot = Nothing
            End SyncLock
        End Sub

        Private Shared Function CurrentLocked() As VolumePresence
            If _testPresence IsNot Nothing Then Return _testPresence
            Dim now = Environment.TickCount64
            If _snapshot Is Nothing OrElse now - _snapshotTicks > SnapshotLifetimeMs Then
                _snapshot = VolumePresence.Snapshot()
                _snapshotTicks = now
            End If
            Return _snapshot
        End Function

    End Class

End Namespace
