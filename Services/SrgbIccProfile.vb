Imports System
Imports System.IO
Imports System.Text

Namespace Services

    ''' <summary>Ein sRGB-Farbprofil als ICC-Bytes, zum Einbetten in Dateien, die ihr Profil
    ''' selbst tragen muessen (PSD, Ressource 1039).
    '''
    ''' WARUM SELBST GEBAUT: Skia schreibt fuer sRGB beim PNG-Kodieren nur das kleine sRGB-Stueck und
    ''' kein Profil, und eine Abfrage der Bytes eines Farbraums gibt SkiaSharp nicht her. Ein fertiges
    ''' Profil aus einer fremden Quelle einzubetten hiesse, eine Lizenz mitzutragen. Der Inhalt ist
    ''' dagegen vollstaendig durch die Norm festgelegt (IEC 61966-2-1) und in wenigen Feldern zu
    ''' schreiben.
    '''
    ''' AUFBAU: ICC v2.1, Geraeteklasse Monitor, Farbraum RGB, Verbindungsraum XYZ. Primaerfarben nach
    ''' D50 angepasst (Bradford), wie ICC es fuer den Verbindungsraum verlangt; Weisspunkt D50; die
    ''' sRGB-Kurve als Tabelle mit 1024 Stuetzwerten, fuer alle drei Kanaele dieselbe. Version 2 und
    ''' keine parametrische Kurve, weil die erst mit Version 4 kommt und aeltere Programme sie nicht
    ''' lesen. Geprueft wird das Profil an Skia selbst: es muss es als sRGB erkennen und Farben
    ''' darueber unveraendert lassen.</summary>
    Public NotInheritable Class SrgbIccProfile

        Private Sub New()
        End Sub

        Private Shared ReadOnly Cached As Byte() = Build()

        ''' <summary>Die Bytes des Profils. Jeder Aufruf bekommt eine eigene Kopie.</summary>
        Public Shared Function Bytes() As Byte()
            Return CType(Cached.Clone(), Byte())
        End Function

        Private Const CurvePoints As Integer = 1024
        Private Const Description As String = "sRGB IEC61966-2.1"
        Private Const Copyright As String = "No copyright, use freely"

        Private Shared Function Build() As Byte()
            ' Die Tags in der Reihenfolge, in der ihre Daten folgen; die drei Kurven teilen sich EINE.
            Dim desc = DescriptionTag(Description)
            Dim cprt = TextTag(Copyright)
            Dim wtpt = XyzTag(0.9642, 1.0, 0.8249)
            Dim rXyz = XyzTag(0.4360747, 0.2225045, 0.0139322)
            Dim gXyz = XyzTag(0.3850649, 0.7168786, 0.0971045)
            Dim bXyz = XyzTag(0.1430804, 0.0606169, 0.7141733)
            Dim trc = CurveTag()

            Dim blocks = New(Signature As String, Data As Byte())() {
                ("desc", desc), ("cprt", cprt), ("wtpt", wtpt), ("rXYZ", rXyz), ("gXYZ", gXyz), ("bXYZ", bXyz), ("rTRC", trc)}
            Dim tagCount = blocks.Length + 2   ' gTRC und bTRC zeigen auf die Daten von rTRC
            Dim offset = 128 + 4 + tagCount * 12
            Dim offsets(blocks.Length - 1) As Integer
            For i = 0 To blocks.Length - 1
                offsets(i) = offset
                offset += Align4(blocks(i).Data.Length)
            Next
            Dim total = offset

            Using ms As New MemoryStream()
                ' Kopf, 128 Byte.
                WriteU32(ms, total)
                WriteU32(ms, 0)                       ' bevorzugtes Farbmodul: keines
                WriteU32(ms, &H2100000)               ' Version 2.1
                WriteText(ms, "mntr") : WriteText(ms, "RGB ") : WriteText(ms, "XYZ ")
                For Each v In {2026, 1, 1, 0, 0, 0}   ' Erstellungsdatum, fest: das Profil aendert sich nicht
                    WriteU16(ms, v)
                Next
                WriteText(ms, "acsp")
                WriteU32(ms, 0) : WriteU32(ms, 0)     ' Plattform, Merkmale
                WriteU32(ms, 0) : WriteU32(ms, 0)     ' Hersteller, Modell
                WriteU32(ms, 0) : WriteU32(ms, 0)     ' Geraeteattribute
                WriteU32(ms, 0)                       ' Wiedergabeabsicht: wahrnehmungsorientiert
                WriteS15Fixed16(ms, 0.9642) : WriteS15Fixed16(ms, 1.0) : WriteS15Fixed16(ms, 0.8249)
                WriteU32(ms, 0)                       ' Ersteller
                ms.Write(New Byte(43) {}, 0, 44)      ' Rest des Kopfs bis 128

                ' Tag-Tabelle.
                WriteU32(ms, tagCount)
                For i = 0 To blocks.Length - 1
                    WriteText(ms, blocks(i).Signature)
                    WriteU32(ms, offsets(i))
                    WriteU32(ms, blocks(i).Data.Length)
                Next
                Dim trcIndex = blocks.Length - 1
                For Each sharedTag In {"gTRC", "bTRC"}
                    WriteText(ms, sharedTag)
                    WriteU32(ms, offsets(trcIndex))
                    WriteU32(ms, blocks(trcIndex).Data.Length)
                Next

                ' Die Daten, je auf vier Byte aufgefuellt.
                For Each block In blocks
                    ms.Write(block.Data, 0, block.Data.Length)
                    For pad = block.Data.Length To Align4(block.Data.Length) - 1
                        ms.WriteByte(0)
                    Next
                Next
                Return ms.ToArray()
            End Using
        End Function

        ''' <summary>textDescriptionType der Version 2: ASCII mit Nullbyte, dahinter leere Unicode-
        ''' und ScriptCode-Teile. Die Laengen der leeren Teile sind fest vorgeschrieben.</summary>
        Private Shared Function DescriptionTag(text As String) As Byte()
            Using ms As New MemoryStream()
                WriteText(ms, "desc") : WriteU32(ms, 0)
                Dim ascii = Encoding.ASCII.GetBytes(text)
                WriteU32(ms, ascii.Length + 1)
                ms.Write(ascii, 0, ascii.Length) : ms.WriteByte(0)
                WriteU32(ms, 0) : WriteU32(ms, 0)    ' Unicode: Sprache, Anzahl
                WriteU16(ms, 0) : ms.WriteByte(0)    ' ScriptCode: Code, Anzahl
                ms.Write(New Byte(66) {}, 0, 67)     ' ScriptCode: 67 Byte Platz
                Return ms.ToArray()
            End Using
        End Function

        Private Shared Function TextTag(text As String) As Byte()
            Using ms As New MemoryStream()
                WriteText(ms, "text") : WriteU32(ms, 0)
                Dim ascii = Encoding.ASCII.GetBytes(text)
                ms.Write(ascii, 0, ascii.Length) : ms.WriteByte(0)
                Return ms.ToArray()
            End Using
        End Function

        Private Shared Function XyzTag(x As Double, y As Double, z As Double) As Byte()
            Using ms As New MemoryStream()
                WriteText(ms, "XYZ ") : WriteU32(ms, 0)
                WriteS15Fixed16(ms, x) : WriteS15Fixed16(ms, y) : WriteS15Fixed16(ms, z)
                Return ms.ToArray()
            End Using
        End Function

        ''' <summary>Die sRGB-Kurve (kodiert nach linear) als Tabelle: unterhalb 0,04045 linear, darueber
        ''' die Potenz 2,4 mit Versatz.</summary>
        Private Shared Function CurveTag() As Byte()
            Using ms As New MemoryStream()
                WriteText(ms, "curv") : WriteU32(ms, 0)
                WriteU32(ms, CurvePoints)
                For i = 0 To CurvePoints - 1
                    Dim v = i / CDbl(CurvePoints - 1)
                    Dim linear = If(v <= 0.04045, v / 12.92, Math.Pow((v + 0.055) / 1.055, 2.4))
                    WriteU16(ms, CInt(Math.Round(Math.Max(0.0, Math.Min(1.0, linear)) * 65535.0)))
                Next
                Return ms.ToArray()
            End Using
        End Function

        Private Shared Function Align4(n As Integer) As Integer
            Return (n + 3) And Not 3
        End Function

        Private Shared Sub WriteText(ms As Stream, s As String)
            Dim b = Encoding.ASCII.GetBytes(s)
            ms.Write(b, 0, b.Length)
        End Sub

        Private Shared Sub WriteU16(ms As Stream, v As Integer)
            ms.WriteByte(CByte((v >> 8) And &HFF))
            ms.WriteByte(CByte(v And &HFF))
        End Sub

        Private Shared Sub WriteU32(ms As Stream, v As Integer)
            ms.WriteByte(CByte((v >> 24) And &HFF))
            ms.WriteByte(CByte((v >> 16) And &HFF))
            ms.WriteByte(CByte((v >> 8) And &HFF))
            ms.WriteByte(CByte(v And &HFF))
        End Sub

        ''' <summary>Festkomma s15.16, wie ICC alle XYZ-Werte ablegt.</summary>
        Private Shared Sub WriteS15Fixed16(ms As Stream, v As Double)
            WriteU32(ms, CInt(Math.Round(v * 65536.0)))
        End Sub

    End Class

End Namespace
