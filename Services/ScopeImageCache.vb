Imports System
Imports System.Collections.Generic
Imports SkiaSharp

Namespace Services

    ''' <summary>Zwischenspeicher für die Analysebilder EINES Bildstandes.
    '''
    ''' Das Rechnen eines Analysebildes kostet den Decode der Quelle und einen Durchgang über alle
    ''' Bildpunkte. Beides fiel bisher bei JEDEM Anlass erneut an: beim Umschalten zwischen
    ''' Histogramm, Waveform und Parade, beim Hin- und Herwechseln des Werkzeugs, und seit es das
    ''' Analysebild an zwei Orten gibt, auch dann, wenn dieselbe Frage schon einmal beantwortet war.
    '''
    ''' DER SCHLÜSSEL IST DER BILDSTAND, nicht das Bild. Solange sich an den Reglern nichts ändert,
    ''' bleibt die Szene dieselbe, und dann gilt auch das gerechnete Diagramm weiter. Der Aufrufer
    ''' bestimmt, was "derselbe Stand" heißt: Galerie und Betrachter nehmen Pfad und Änderungszeit,
    ''' der Editor die laufende Nummer seiner Szene. Ein neuer Stand wirft den alten weg - es geht
    ''' nicht darum, eine Geschichte zu halten, sondern darum, dieselbe Rechnung nicht dreimal zu
    ''' machen.
    '''
    ''' GEHALTEN WIRD DAS SKIA-BILD, nicht die Anzeige-Bitmap. Die Anzeige gibt ihr Bild frei,
    ''' sobald ein neues kommt (siehe InfoPanelViewModel.ScopeImage); eine Instanz, die zugleich im
    ''' Zwischenspeicher steht, wäre danach tot. Der Speicher gibt deshalb sein Skia-Bild nie aus
    ''' der Hand - der Aufrufer bekommt eine frische Anzeige-Bitmap daraus, und die kostet eine
    ''' Kopie von 720 KB statt eines Decodes von Sekunden.
    '''
    ''' Die Obergrenze ergibt sich von selbst: drei Darstellungen mal ein Maß, also rund zwei
    ''' Megabyte. Ein zweites Maß (die grosse Ansicht) käme dazu, deshalb steht es im Schlüssel.</summary>
    Public NotInheritable Class ScopeImageCache

        Private Sub New()
        End Sub

        Private Shared ReadOnly _lock As New Object()
        ''' <summary>Der Bildstand, für den die Einträge unten gelten. Leer = nichts gemerkt.</summary>
        Private Shared _sourceKey As String = ""
        ''' <summary>Darstellung samt Maß -> gerechnetes Bild.</summary>
        Private Shared ReadOnly _images As New Dictionary(Of String, SKBitmap)(StringComparer.Ordinal)

        Private Shared Function EntryKey(mode As String, width As Integer, height As Integer) As String
            Return $"{mode}|{width}x{height}"
        End Function

        ''' <summary>Eine EIGENE Kopie des gemerkten Bilds zu diesem Stand, oder Nothing.
        '''
        ''' Die Kopie entsteht noch unter der Sperre: ein anderer Lauf darf beim Wechsel des
        ''' Bildstands die Cache-Bitmap sofort freigeben, während der Aufrufer seine Kopie in eine
        ''' Avalonia-Bitmap überträgt. Eine Referenz auf das cacheeigene Bild hinauszugeben wäre
        ''' dabei ein Use-after-dispose-Rennen.</summary>
        Public Shared Function TryGetCopy(sourceKey As String, mode As String,
                                          width As Integer, height As Integer) As SKBitmap
            If String.IsNullOrEmpty(sourceKey) Then Return Nothing
            SyncLock _lock
                If Not String.Equals(_sourceKey, sourceKey, StringComparison.Ordinal) Then Return Nothing
                Dim found As SKBitmap = Nothing
                If _images.TryGetValue(EntryKey(mode, width, height), found) AndAlso found IsNot Nothing Then
                    Return found.Copy()
                End If
                Return Nothing
            End SyncLock
        End Function

        ''' <summary>Merkt ein gerechnetes Bild. Der Speicher ÜBERNIMMT es; der Aufrufer gibt es
        ''' nicht mehr frei. Ein anderer Bildstand wirft die bisherigen Einträge weg.</summary>
        Public Shared Sub Put(sourceKey As String, mode As String,
                              width As Integer, height As Integer, image As SKBitmap)
            If image Is Nothing Then Return
            If String.IsNullOrEmpty(sourceKey) Then
                ' Ohne Stand kein Merken - und das Bild gehört dann auch niemandem sonst.
                image.Dispose()
                Return
            End If
            SyncLock _lock
                If Not String.Equals(_sourceKey, sourceKey, StringComparison.Ordinal) Then
                    DisposeAllLocked()
                    _sourceKey = sourceKey
                End If
                Dim key = EntryKey(mode, width, height)
                Dim previous As SKBitmap = Nothing
                If _images.TryGetValue(key, previous) AndAlso previous IsNot Nothing Then
                    ' Zwei Läufe für dieselbe Frage können sich überholen. Der spätere gewinnt,
                    ' der frühere wird hier freigegeben - beide zeigen dasselbe.
                    previous.Dispose()
                End If
                _images(key) = image
            End SyncLock
        End Sub

        ''' <summary>Alles vergessen. Beim Schliessen eines Bildes und wenn der Speicher zur Last
        ''' wird; das nächste Analysebild rechnet dann wieder von vorn.</summary>
        Public Shared Sub Clear()
            SyncLock _lock
                DisposeAllLocked()
                _sourceKey = ""
            End SyncLock
        End Sub

        Private Shared Sub DisposeAllLocked()
            For Each entry In _images
                entry.Value?.Dispose()
            Next
            _images.Clear()
        End Sub

        ''' <summary>Kennung eines Bildstandes aus einer DATEI: Pfad und Änderungszeit. Die Zeit
        ''' gehört dazu, sonst zeigte eine ausserhalb bearbeitete Datei weiter das alte Diagramm.</summary>
        Public Shared Function FileSourceKey(path As String) As String
            If String.IsNullOrEmpty(path) Then Return ""
            Try
                Return $"file:{path}|{IO.File.GetLastWriteTimeUtc(path).Ticks}"
            Catch
                ' Keine Auskunft über die Datei - dann lieber nicht merken als falsch merken.
                Return ""
            End Try
        End Function

    End Class

End Namespace
