Imports System
Imports System.Collections.Generic
Imports System.Threading.Tasks
Imports Avalonia.Media.Imaging
Imports FerrumPix.Services

Namespace ViewModels

    ' Die Nachbarbilder im Betrachter vorentwickeln (ViewerNeighborPrefetch): nach jedem gezeigten
    ' Bild die drei naechsten in Laufrichtung und eins zurueck, solange der Betrachter RAWs selbst
    ' entwickelt. Blaettert man zu einem davon, kommt es aus dem Vorlauf statt aus einem neuen Decode
    ' (RunBitmapLoad). Das verlassene Bild geht als "eins zurueck" an den Vorlauf, statt freigegeben
    ' zu werden (OfferShownToNeighborPrefetch).
    '
    ' Aus bleibt der Vorlauf in der Diashow (sie hat ihren eigenen fuer genau ein Bild,
    ' StartSlideshowPrefetch), im Vergleich, in einer Immich-Sitzung, fuer zusaetzliche Varianten und
    ' solange der Betrachter nicht zu sehen ist (SetViewActive): dann gibt er seinen Speicher frei.
    Partial Public Class ViewerViewModel

        Private _neighborPrefetch As ViewerNeighborPrefetch
        Private _neighborPrefetchViewActive As Boolean = True

        ''' Das Bild, das gerade an den Vorlauf ging; der Setzer von CurrentImage gibt es nicht frei.
        Private _handedToNeighborPrefetch As Bitmap

        ''' Der Schluessel des gezeigten entwickelten Bildes, vom Beginn seiner Entwicklung, fuer die
        ''' Uebergabe an den Vorlauf beim Weiterblaettern; mit Aufloesung, zu der er gehoert.
        Private _shownPrefetchKey As Task(Of String)
        Private _shownPrefetchReduced As Boolean

        Friend ReadOnly Property NeighborPrefetch As ViewerNeighborPrefetch
            Get
                If _neighborPrefetch Is Nothing Then
                    _neighborPrefetch = New ViewerNeighborPrefetch(
                        Function(path, reduced) DecodeViewerBitmapWithState(path, reducedRawDecode:=reduced),
                        AddressOf WillDevelopRawInViewer,
                        AddressOf DisposeDeferred)
                End If
                Return _neighborPrefetch
            End Get
        End Property

        ''' <summary>Darf der Vorlauf jetzt laufen? Liest die Einstellung bei jeder Frage, damit ein
        ''' umgelegter Schalter beim naechsten Bild wirkt.</summary>
        Private Function IsNeighborPrefetchAllowed() As Boolean
            ' Das Vollbild ist eine angehaltene Diashow (EnterPausedSlideshowSession); dort blaettert
            ' man von Hand und der Vorlauf gilt. Nur eine laufende Diashow hat ihren eigenen, und eine
            ' gemischte folgt nicht der Reihe.
            If Not _neighborPrefetchViewActive OrElse _isSlideshowPlaying OrElse (_slideshowSession AndAlso IsSlideshowShuffle) OrElse
               _isCompareMode OrElse _isImmichSession Then Return False
            Dim settings = AppSettingsService.Load()
            Return settings.PrefetchNeighborsInViewer AndAlso
                   (settings.DevelopRawInViewer OrElse settings.DevelopRawInViewerWithoutRecipe)
        End Function

        ''' <summary>Betrachter sichtbar oder nicht. Unsichtbar haelt der Vorlauf an und gibt seinen
        ''' Speicher frei, etwa fuer den Editor.</summary>
        Private Sub SetNeighborPrefetchViewActive(active As Boolean)
            _neighborPrefetchViewActive = active
            If Not active Then _neighborPrefetch?.Clear()
        End Sub

        ''' <summary>Meldet dem Vorlauf einen Schritt von <paramref name="fromIndex"/> nach
        ''' <paramref name="toIndex"/>; die Reihe laeuft ringsum, der kuerzere Weg gibt die Richtung.</summary>
        Private Sub NoteNeighborPrefetchStep(fromIndex As Integer, toIndex As Integer)
            Dim count = _folderPaths.Count
            If _neighborPrefetch Is Nothing OrElse count < 2 OrElse fromIndex < 0 OrElse fromIndex = toIndex Then Return
            Dim forward = (((toIndex - fromIndex) Mod count) + count) Mod count
            _neighborPrefetch.NoteStep(If(forward <= count \ 2, 1, -1))
        End Sub

        ''' <summary>Nach einem gezeigten Bild: die Ziele des Vorlaufs neu setzen, oder ihn anhalten.</summary>
        Private Sub ScheduleNeighborPrefetch(path As String, reduced As Boolean, shown As Bitmap)
            If Not IsNeighborPrefetchAllowed() OrElse shown Is Nothing Then
                _neighborPrefetch?.Clear()
                Return
            End If
            Dim prefetch = NeighborPrefetch
            Dim targets As New List(Of String)()
            For Each index In ViewerNeighborPrefetch.TargetIndices(_currentIndex, _folderPaths.Count, prefetch.Direction, wrap:=True)
                Dim entry = _folderPaths(index)
                ' Eine Variante haengt am Rezept ihres Schluessels, das Vorgeladene am Pfad der Datei.
                If String.IsNullOrEmpty(entry) OrElse VariantKey.IsVariant(entry) OrElse VideoPreviewService.IsSupportedVideo(entry) Then Continue For
                targets.Add(entry)
            Next
            Dim bytes = CLng(shown.PixelSize.Width) * shown.PixelSize.Height * 4
            ' In voller Aufloesung werfen die Vorlaeufe den 16-Bit-Zwischenspeicher des Editors um;
            ' nach ihnen fuellt der Vorlauf ihn fuer das gezeigte Bild wieder (statt des Vorwaermens
            ' nach der Verweilzeit).
            prefetch.Schedule(targets, reduced, path, If(reduced, Nothing, path), bytes)
        End Sub

        ''' <summary>Laeuft das Vorwaermen fuer den Editor ueber den Vorlauf (volle Aufloesung)?</summary>
        Private Function NeighborPrefetchWarmsEditor(reduced As Boolean) As Boolean
            Return Not reduced AndAlso IsNeighborPrefetchAllowed()
        End Function

        ''' <summary>Beim Verlassen eines entwickelten Bildes: es geht an den Vorlauf, statt freigegeben
        ''' zu werden. Nur, wenn es wirklich das fertig gezeigte Bild dieser Datei ist und der neue
        ''' Pfad ein anderer.</summary>
        Private Sub OfferShownToNeighborPrefetch(nextPath As String)
            If _neighborPrefetch Is Nothing OrElse _isBitmapLoading OrElse _shownPrefetchKey Is Nothing Then Return
            Dim shown = _currentImage
            If shown Is Nothing OrElse Not Object.ReferenceEquals(shown, _rawDevelopedImage) Then Return
            If String.IsNullOrEmpty(_shownBitmapPath) OrElse String.Equals(_shownBitmapPath, nextPath, PathIdentity.Comparison) Then Return
            If Not IsNeighborPrefetchAllowed() Then Return
            If Not _neighborPrefetch.Offer(_shownBitmapPath, _shownPrefetchReduced, shown, True, _shownPrefetchKey) Then Return
            _handedToNeighborPrefetch = shown
            _rawDevelopedImage = Nothing
            If Object.ReferenceEquals(_editorHandoffSource, shown) Then _editorHandoffSource = Nothing
            _shownPrefetchKey = Nothing
        End Sub

    End Class

End Namespace
