Imports System
Imports System.IO

Namespace Services

    ' Die Kennung einer Entwicklung fuer Abnehmer AUSSERHALB dieses Dienstes, die ein fertiges Bild
    ' laenger halten als der Decode selbst (das Vorentwickeln der Nachbarbilder im Betrachter).
    '
    ' Sie setzt sich aus denselben Groessen zusammen, nach denen der 8-Bit-Zwischenspeicher in
    ' DecodeIntern einen Treffer annimmt, und fragt sie ueber dieselben Funktionen ab: das
    ' Demosaic-Verfahren samt X-Trans-Durchgaengen (ConfiguredDemosaic), das Entklemmen, die Schalter
    ' der Farbstufe (ColorPathState), die Grundbelichtung der Kamera und die Kennlinien des Objektivs.
    ' Dazu kommt die Verzeichnung, die im Zwischenspeicher fehlt, weil sie dort erst hinter ihm
    ' laeuft; im fertigen Bild steckt sie. Wer im Schluessel des Zwischenspeichers eine Groesse
    ' aufnimmt, nimmt sie hier mit auf.
    Partial Public NotInheritable Class RawDecodeService

        ''' <summary>Woran ein fertig entwickeltes Bild dieser Datei erkennt, ob die Einstellungen der
        ''' Entwicklung noch dieselben sind, mit den Vorgaben der Einstellungen (keine Wahl je Bild:
        ''' die steht im Rezept, und dessen Stand pruefen die Abnehmer selbst). Liest die Kopfdaten
        ''' der Datei, gehoert also nicht auf den Oberflaechenfaden. Leer, wenn die Datei nicht zu
        ''' entwickeln ist.</summary>
        Friend Shared Function DevelopmentKey(path As String) As String
            If String.IsNullOrEmpty(path) OrElse Not CanDevelop(path) Then Return ""
            Dim c = Globalization.CultureInfo.InvariantCulture
            Try
                Dim lens = LensCorrectionForFile(path, Nothing)
                Return String.Join("|",
                                   File.GetLastWriteTimeUtc(path).Ticks.ToString(c),
                                   ConfiguredDemosaic().ToString(c),
                                   ConfiguredHighlightUnclip().ToString(),
                                   ColorPathState().ToString(c),
                                   BaseExposureForFile(path).ToString("R", c),
                                   LensKey(lens),
                                   (lens IsNot Nothing AndAlso lens.HasDistortion).ToString())
            Catch
                Return ""
            End Try
        End Function

    End Class

End Namespace
