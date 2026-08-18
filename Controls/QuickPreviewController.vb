Imports System
Imports System.Threading.Tasks
Imports Avalonia.Controls
Imports Avalonia.Media.Imaging
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace Controls

    ''' <summary>Die Schnellvorschau: mittlere Maustaste in der Galerie, Leertaste ebendort, und
    ''' dasselbe am Filmstreifen von Betrachter und Editor.
    '''
    ''' EIN Weg fuer beide Stellen, weil beide dieselben drei Fallen haben und sie vorher jede fuer
    ''' sich (und unvollstaendig) umgangen haben:
    '''
    ''' 1. **Ein spaeter Decode darf keine neuere Vorschau ueberschreiben.** Wer A oeffnet, schliesst
    '''    und schnell B oeffnet, bekam das Bild von A zu sehen, sobald dessen Decode fertig war -
    '''    geprueft wurde nur, ob das Overlay ueberhaupt sichtbar ist, und das ist es ja wieder.
    '''    Jede Anzeige zieht deshalb eine Nummer; wer nicht mehr die letzte hat, legt sein Ergebnis
    '''    weg.
    ''' 2. **Das dekodierte Bild gehoert freigegeben.** Es ist ein Vollbild und damit gross; weder
    '''    beim Ersetzen durch die naechste Vorschau noch beim Schliessen waehrend eines laufenden
    '''    Decodes wurde es abgeraeumt. Wiederholte Vorschauen liessen den Speicher wachsen. Was der
    '''    Vorschau GEHOERT, ist genau die eigene Bitmap - das Vorschaubild des Elements
    '''    (<see cref="ImageItem.Thumbnail"/>) gehoert dem Element und wird nie freigegeben.
    ''' 3. **Ein Serverbild liegt auf keiner Platte.** Sein Pfad ist eine Kennung; der Dekoder
    '''    lieferte dazu still nichts, und sichtbar blieb das Vorschaubild. Geholt wird das Original
    '''    nur auf die ausdrueckliche Geste hin, eine schon geholte Kopie wird wiederverwendet.
    '''
    ''' Die Reihenfolge beim Freigeben ist nicht beliebig: ERST aus der Anzeige nehmen, DANN
    ''' freigeben - sonst zeichnet die Oberflaeche noch auf eine Bitmap, die es nicht mehr gibt.</summary>
    Public NotInheritable Class QuickPreviewController

        ''' <summary>Die Nummer der zuletzt angeforderten Vorschau. Jedes Schliessen zaehlt sie
        ''' ebenfalls hoch - ein laufender Decode gilt danach als ueberholt.</summary>
        Private _run As Integer = 0

        ''' <summary>Die Bitmap, die DIESE Vorschau selbst dekodiert hat. Nur sie darf freigegeben
        ''' werden.</summary>
        Private _ownedBitmap As Bitmap = Nothing

        ''' <summary>Vorschau oeffnen: sofort das vorhandene Vorschaubild zeigen, das volle Bild
        ''' laeuft nach.</summary>
        Public Sub Show(overlay As Panel, image As Avalonia.Controls.Image, item As ImageItem)
            If overlay Is Nothing OrElse image Is Nothing OrElse item Is Nothing Then Return
            _run += 1
            Dim run = _run
            ReleaseOwned(image)
            image.Source = item.Thumbnail
            overlay.IsVisible = True
            LoadFullAsync(overlay, image, item, run)
        End Sub

        ''' <summary>Vorschau schliessen. Ein noch laufender Decode gilt damit als ueberholt und
        ''' legt sein Ergebnis weg, statt es spaeter ueber eine andere Vorschau zu legen.</summary>
        Public Sub Hide(overlay As Panel, image As Avalonia.Controls.Image)
            _run += 1
            If overlay IsNot Nothing Then overlay.IsVisible = False
            ReleaseOwned(image)
        End Sub

        ''' <summary>Ist gerade eine Vorschau offen?</summary>
        Public Shared Function IsOpen(overlay As Panel) As Boolean
            Return overlay IsNot Nothing AndAlso overlay.IsVisible
        End Function

        Private Sub ReleaseOwned(image As Avalonia.Controls.Image)
            If _ownedBitmap Is Nothing Then Return
            If image IsNot Nothing AndAlso ReferenceEquals(image.Source, _ownedBitmap) Then image.Source = Nothing
            _ownedBitmap.Dispose()
            _ownedBitmap = Nothing
        End Sub

        ''' <summary>Eigenes Try, weil es ein <c>Async Sub</c> ist: eine Ausnahme darin landet sonst
        ''' beim Dispatcher und beendet den Prozess.</summary>
        Private Async Sub LoadFullAsync(overlay As Panel, image As Avalonia.Controls.Image,
                                        item As ImageItem, run As Integer)
            ' Ein Video laesst sich nicht als Vollbild-Bitmap dekodieren - das wuerde nur leise
            ' fehlschlagen. Sichtbar bleibt das Standbild aus dem Vorschaubild.
            If item.IsVideoFile Then Return
            Dim bmp As Bitmap = Nothing
            Try
                Dim path = If(item.IsRemoteAsset, Await item.EnsureLocalOriginalAsync(), item.FilePath)
                If String.IsNullOrEmpty(path) Then Return
                If run <> _run OrElse Not overlay.IsVisible Then Return

                ' Auto-Variante: erkennt Buendel (Komposit), RAW, PSD und ueber den HEIF-Zweig auch
                ' HEIC/HEIF/AVIF.
                '
                ' RAW BEKOMMT HIER KEINEN EIGENEN ZWEIG. Er stand einmal in der Galerie und nahm der
                ' Schnellvorschau zwei Dinge, die die Auto-Variante mitbringt:
                '
                ' 1. die Drehung aus dem Sidecar (RawSidecarService.ReadRotationDegrees) - eine im
                '    Betrachter gedrehte RAW steckt ihre Drehung nicht in die Pixel, sondern neben
                '    die Datei. Ohne das stand sie hier wieder ungedreht.
                ' 2. rawContainerPath, ueber das RawPreviewOrigin die Orientierung des CONTAINERS
                '    heranzieht, wenn die eingebettete Vorschau kein eigenes Tag traegt. Ohne das
                '    steht die Vorschau quer zur Entwicklung.
                bmp = Await Task.Run(Function() ImageOrientationService.LoadOrientedAvaloniaBitmapAuto(path))
                If bmp Is Nothing Then Return
                ' UEBERHOLT: inzwischen wurde geschlossen oder eine andere Vorschau geoeffnet. Das
                ' Ergebnis gehoert dann niemandem mehr und geht im Finally weg.
                If run <> _run OrElse Not overlay.IsVisible Then Return

                ReleaseOwned(image)
                image.Source = bmp
                _ownedBitmap = bmp
                ' Ab hier gehoert sie der Anzeige - das Finally darf sie nicht mehr abraeumen.
                bmp = Nothing
            Catch ex As Exception
                DiagnosticLogService.LogException("QuickPreview", ex)
            Finally
                bmp?.Dispose()
            End Try
        End Sub
    End Class

End Namespace
