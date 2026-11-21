Imports System.Globalization

Namespace Services

    ''' <summary>Der Text der Anzeige an einer Pipette, je nach Einstellung
    ''' AppSettings.PickerReadout: "#RRGGBB", "R 12  G 34  B 56" oder beides in zwei Zeilen.
    '''
    ''' <para>ALPHA NUR, WENN DER PUNKT NICHT VOLL DECKT: dann "#RRGGBBAA" und ein angehaengtes
    ''' "A 128", bei beidem in beiden Zeilen. Voll deckend bleibt die Anzeige kurz, denn beim
    ''' Fotografieren ist fast jeder Punkt deckend. Ob eine Pipette Alpha ueberhaupt uebernimmt,
    ''' entscheidet sie selbst: eine, die nur Farbe misst, reicht hier 255 herein.</para>
    '''
    ''' <para>Liegt hier und nicht im Code-Behind, damit der Pruefstand die Textbildung ohne
    ''' Oberflaeche pruefen kann.</para>
    '''
    ''' <para>DIE BREITE SPRINGT NICHT BEIM ZIEHEN: jede Zahl wird links mit Ziffernleerzeichen
    ''' (U+2007, so breit wie eine Ziffer) auf drei Stellen aufgefuellt. Zusammen mit gleich breiten
    ''' Ziffern (die Anzeige schaltet das Schriftmerkmal tnum ein) bleibt die Zeile gleich lang,
    ''' ob unter der Spitze 5 oder 255 steht. R, G, B und A sind Kanalnamen, keine Woerter, und
    ''' werden nicht uebersetzt.</para></summary>
    Public Module PickerReadoutFormatter

        Private ReadOnly PickerFigureSpace As Char = ChrW(&H2007)

        ''' Eigener Name statt "Format": Mitglieder eines Moduls stehen im ganzen Namensraum, ein
        ''' blosses Format verdeckte dort Strings.Format.
        Public Function FormatPickerReadout(mode As String, r As Byte, g As Byte, b As Byte,
                                            Optional a As Byte = 255) As String
            Dim translucent = a < 255
            Dim hex = If(translucent,
                         String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", r, g, b, a),
                         String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", r, g, b))
            Dim rgb = $"R {PadChannel(r)}  G {PadChannel(g)}  B {PadChannel(b)}"
            If translucent Then rgb &= $"  A {PadChannel(a)}"
            Select Case AppSettingsService.NormalizePickerReadout(mode)
                Case "Hex" : Return hex
                Case "Rgb" : Return rgb
                Case Else : Return hex & vbLf & rgb
            End Select
        End Function

        Private Function PadChannel(value As Byte) As String
            Return value.ToString(CultureInfo.InvariantCulture).PadLeft(3, PickerFigureSpace)
        End Function

    End Module

End Namespace
