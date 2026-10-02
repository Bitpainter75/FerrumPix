Imports System

Namespace Models

    ''' <summary>
    ''' Erkennt ein Mausrad-Ereignis, das keine Geste ist, sondern ein Messfehler.
    '''
    ''' Unter X11 rechnet Avalonia das Delta als Differenz zweier absoluter Zaehlerstaende der
    ''' Scroll-Achse (XI2-Valuator geteilt durch dessen Increment). Springt der Zaehler, etwa nach
    ''' einem Geraetewechsel oder mit veraltetem Startwert, kommt EIN Ereignis mit einem Delta in
    ''' Milliardenhoehe an (gemessen: -1,38e9), typisch beim ersten Radschritt nach dem Start.
    '''
    ''' Zwei Folgen, beide gesehen: wer das Delta aufsummiert und in Rastungen abtraegt, haengt den
    ''' UI-Faden auf (Filmstreifen, Karte); und wer es mit einer Schrittweite malnimmt und auf den
    ''' Rollbereich begrenzt, springt mit einem Schlag ans Ende (die Galerie gleich nach dem Start,
    ''' Nutzerbefund, und genauso jeder ScrollViewer). Die Begrenzung verhindert den Haenger, nicht
    ''' den Sprung.
    '''
    ''' Deshalb wird ein solches Ereignis ganz verworfen, nicht gekappt: gekappt liefe es als Sprung um
    ''' die Grenze durch. App.DropImplausibleWheelEvents verschluckt es fuer die ganze Anwendung am
    ''' Fenster; Filmstreifen und Karte pruefen zusaetzlich selbst, weil sie summieren.
    ''' </summary>
    Public NotInheritable Class WheelDeltaGuard

        Private Sub New()
        End Sub

        ''' <summary>Obergrenze fuer das Delta EINES Ereignisses, in Rastungen. Eine echte schnelle
        ''' Geste liefert viele kleine Ereignisse, keines davon auch nur in der Naehe.</summary>
        Public Const MaxPlausibleNotches As Double = 20.0

        Public Shared Function IsImplausible(delta As Double) As Boolean
            Return Double.IsNaN(delta) OrElse Double.IsInfinity(delta) OrElse Math.Abs(delta) > MaxPlausibleNotches
        End Function

        Public Shared Function IsImplausible(delta As Avalonia.Vector) As Boolean
            Return IsImplausible(delta.X) OrElse IsImplausible(delta.Y)
        End Function

    End Class

End Namespace
