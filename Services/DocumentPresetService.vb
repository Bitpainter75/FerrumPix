Imports System.Collections.Generic
Imports System.Linq

Namespace Services

    ''' <summary>
    ''' Die Vorgaben für „Neues Bild": Foto-, Bildschirm- und Papierformate samt der Umrechnung
    ''' physischer Maße in Pixel.
    '''
    ''' Warum die Papierformate hier NOCH EINMAL stehen, obwohl PrintService sie kennt: dort sind
    ''' sie <c>Private Shared</c> und in PUNKT (1/72 Zoll) hinterlegt, weil der Druckpfad in Punkt
    ''' rechnet. Hier werden Millimeter gebraucht, und der Druckpfad ist zu heikel, um ihn für eine
    ''' zweite Maßeinheit aufzubohren. Ändert sich ein Papierformat, gehört es an BEIDEN Stellen
    ''' gepflegt - dafür gibt es eine Prüfung in der Diagnose.
    ''' </summary>
    Public Class DocumentPresetService

        Public Const MmPerInch As Double = 25.4

        ''' <summary>Gruppenschlüssel - Logik, nie Anzeigetext. Die Überschrift wird in der View übersetzt.</summary>
        Public Const GroupPhoto As String = "Photo"
        Public Const GroupScreen As String = "Screen"
        Public Const GroupPaper As String = "Paper"

        ''' <summary>Eine Vorgabe. Entweder physisch (WidthMm/HeightMm gesetzt) oder in festen Pixeln
        ''' (WidthPx/HeightPx gesetzt) - Bildschirmformate haben keine sinnvolle Millimeter-Angabe.</summary>
        Public Class DocumentPreset
            Public Property Id As String
            Public Property Group As String
            ''' <summary>Deutscher Quelltext; die View übersetzt ihn über LocalizationService.T.</summary>
            Public Property Label As String
            Public Property WidthMm As Double
            Public Property HeightMm As Double
            Public Property WidthPx As Integer
            Public Property HeightPx As Integer

            Public ReadOnly Property IsPixelPreset As Boolean
                Get
                    Return WidthPx > 0 AndAlso HeightPx > 0
                End Get
            End Property

            ''' <summary>Zweite Zeile in der Kachel: „297 × 420 mm" bzw. „1920 × 1080 px".</summary>
            Public ReadOnly Property SizeText As String
                Get
                    If IsPixelPreset Then Return $"{WidthPx} × {HeightPx} px"
                    Return $"{FormatMm(WidthMm)} × {FormatMm(HeightMm)} mm"
                End Get
            End Property

            Private Shared Function FormatMm(value As Double) As String
                If Math.Abs(value - Math.Round(value)) < 0.05 Then Return CInt(Math.Round(value)).ToString()
                Return value.ToString("0.#")
            End Function
        End Class

        ''' <summary>Alle Vorgaben in Anzeigereihenfolge.</summary>
        Public Shared ReadOnly Property All As IReadOnlyList(Of DocumentPreset) = New List(Of DocumentPreset) From {
            New DocumentPreset With {.Id = "Photo10x15", .Group = GroupPhoto, .Label = "10 × 15 cm", .WidthMm = 100, .HeightMm = 150},
            New DocumentPreset With {.Id = "Photo13x18", .Group = GroupPhoto, .Label = "13 × 18 cm", .WidthMm = 130, .HeightMm = 180},
            New DocumentPreset With {.Id = "Photo20x30", .Group = GroupPhoto, .Label = "20 × 30 cm", .WidthMm = 200, .HeightMm = 300},
            New DocumentPreset With {.Id = "Square1080", .Group = GroupPhoto, .Label = "Quadrat", .WidthPx = 1080, .HeightPx = 1080},
            New DocumentPreset With {.Id = "FullHd", .Group = GroupScreen, .Label = "Full HD", .WidthPx = 1920, .HeightPx = 1080},
            New DocumentPreset With {.Id = "Uhd4K", .Group = GroupScreen, .Label = "4K UHD", .WidthPx = 3840, .HeightPx = 2160},
            New DocumentPreset With {.Id = "A3", .Group = GroupPaper, .Label = "A3", .WidthMm = 297, .HeightMm = 420},
            New DocumentPreset With {.Id = "A4", .Group = GroupPaper, .Label = "A4", .WidthMm = 210, .HeightMm = 297},
            New DocumentPreset With {.Id = "A5", .Group = GroupPaper, .Label = "A5", .WidthMm = 148, .HeightMm = 210},
            New DocumentPreset With {.Id = "Letter", .Group = GroupPaper, .Label = "Letter", .WidthMm = 215.9, .HeightMm = 279.4},
            New DocumentPreset With {.Id = "Legal", .Group = GroupPaper, .Label = "Legal", .WidthMm = 215.9, .HeightMm = 355.6}
        }

        Public Shared Function ByGroup(group As String) As List(Of DocumentPreset)
            Return All.Where(Function(p) String.Equals(p.Group, group, StringComparison.Ordinal)).ToList()
        End Function

        Public Shared Function ById(id As String) As DocumentPreset
            If String.IsNullOrEmpty(id) Then Return Nothing
            Return All.FirstOrDefault(Function(p) String.Equals(p.Id, id, StringComparison.Ordinal))
        End Function

        ''' <summary>Millimeter bei gegebener Auflösung in ganze Pixel. A4 bei 300 dpi = 2480 × 3508.</summary>
        Public Shared Function MmToPixels(mm As Double, dpi As Double) As Integer
            If mm <= 0 OrElse dpi <= 0 Then Return 0
            Return Math.Max(1, CInt(Math.Round(mm / MmPerInch * dpi)))
        End Function

        Public Shared Function PixelsToMm(px As Double, dpi As Double) As Double
            If px <= 0 OrElse dpi <= 0 Then Return 0
            Return px * MmPerInch / dpi
        End Function

        ''' <summary>Rechnet einen Wert der gewählten Einheit in Pixel. "px" geht unverändert durch,
        ''' die physischen Einheiten laufen über die Auflösung.</summary>
        Public Shared Function ToPixels(value As Double, unit As String, dpi As Double) As Integer
            Select Case NormalizeUnit(unit)
                Case "px" : Return Math.Max(1, CInt(Math.Round(value)))
                Case "cm" : Return MmToPixels(value * 10.0, dpi)
                Case "in" : Return MmToPixels(value * MmPerInch, dpi)
                Case Else : Return MmToPixels(value, dpi)
            End Select
        End Function

        ''' <summary>Gegenrichtung zu ToPixels - für das Umschalten der Einheit im Dialog.</summary>
        Public Shared Function FromPixels(px As Double, unit As String, dpi As Double) As Double
            Select Case NormalizeUnit(unit)
                Case "px" : Return Math.Round(px)
                Case "cm" : Return Math.Round(PixelsToMm(px, dpi) / 10.0, 2)
                Case "in" : Return Math.Round(PixelsToMm(px, dpi) / MmPerInch, 2)
                Case Else : Return Math.Round(PixelsToMm(px, dpi), 1)
            End Select
        End Function

        Public Shared Function NormalizeUnit(unit As String) As String
            Select Case If(unit, "").Trim().ToLowerInvariant()
                Case "px", "cm", "in" : Return unit.Trim().ToLowerInvariant()
                Case Else : Return "mm"
            End Select
        End Function

        ''' <summary>Die Vorgabe in Pixel, bei physischen Vorgaben über die Auflösung.</summary>
        Public Shared Function PresetPixels(preset As DocumentPreset, dpi As Double) As (Width As Integer, Height As Integer)
            If preset Is Nothing Then Return (0, 0)
            If preset.IsPixelPreset Then Return (preset.WidthPx, preset.HeightPx)
            Return (MmToPixels(preset.WidthMm, dpi), MmToPixels(preset.HeightMm, dpi))
        End Function

    End Class

End Namespace
