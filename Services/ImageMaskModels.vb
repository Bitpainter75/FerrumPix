Imports System
Imports System.Buffers
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.IO
Imports System.Linq
Imports System.Runtime.CompilerServices
Imports System.Threading
Imports System.Threading.Tasks
Imports SkiaSharp
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports System.Text.RegularExpressions
Imports System.Text.Json.Serialization
Imports System.Runtime.InteropServices
Imports QRCoder

' Die Datenmodelle der Masken: die persistente Maske selbst, ihre Bestandteile und die
' Korrekturebene, die eine Maske traegt. Sie lagen bis 2026-08-06 im Kopf von
' ImageProcessor.vb und teilen keinen Zustand mit dem Prozessor.
' Verwandte Modelle: ImageAnnotationModels.vb (Objekte) und ImageAdjustmentsModels.vb (Rezept).
Namespace Services

    ''' <summary>Persistente, wiederverwendbare Alpha-Maske im ungedrehten Quellbildraum.
    ''' Eine aktive Auswahl ist nur UI-Zustand; lokale Korrekturen verweisen über MaskId auf diese Daten.</summary>
    Public Class ImageMask
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = LocalizationService.T("Auswahlmaske")
        Public Property SourceWidthPixels As Integer
        Public Property SourceHeightPixels As Integer
        Public Property Left As Integer
        Public Property Top As Integer
        Public Property Right As Integer
        Public Property Bottom As Integer

        ' Arbeitsform und Speicherform der Maskenpixel - die Regel dahinter steht EINMAL in
        ' AlphaPixels, nicht hier und in MaskComponent nebeneinander.
        Private ReadOnly _pixels As New AlphaPixels()

        ''' <summary>Die Bildpunkte als PNG - die SPEICHERFORM, nicht die Arbeitsform. Sie entsteht
        ''' erst, wenn jemand sie liest (Rezept schreiben), und wird beim Laden gesetzt. Wer im
        ''' Programm mit der Maske rechnet, nimmt <see cref="Raster"/>.</summary>
        Public Property PngBase64 As String
            Get
                Return _pixels.Png
            End Get
            Set(value As String)
                _pixels.Png = value
            End Set
        End Property

        ''' <summary>Die Bildpunkte im Speicher. Beim Setzen gilt das Raster; das PNG wird erst
        ''' wieder erzeugt, wenn es gebraucht wird.</summary>
        <JsonIgnore>
        Public Property Raster As AlphaRaster
            Get
                Return _pixels.Raster
            End Get
            Set(value As AlphaRaster)
                _pixels.Raster = value
            End Set
        End Property

        ''' <summary>Uebernimmt die Bilddaten einer anderen Maske SO WIE SIE VORLIEGEN - ein PNG
        ''' bleibt PNG, ein Raster bleibt Raster. Eine Abschrift ueber die Eigenschaften wuerde
        ''' sonst genau das Packen ausloesen, das dieser Weg vermeidet.</summary>
        Friend Sub CopyPixelDataFrom(other As ImageMask)
            If other Is Nothing Then Return
            _pixels.CopyFrom(other._pixels)
            _brushAdd.CopyFrom(other._brushAdd)
            _brushSubtract.CopyFrom(other._brushSubtract)
        End Sub

        ''' <summary>Dasselbe von einem Bestandteil her. Ohne diesen Weg liefe jede Zerlegung in
        ''' Bestandteile ueber die Eigenschaft - und die PACKT. GetComponents laeuft bei jedem
        ''' Rastern und bei jeder Zaehlung der Bestandteile.</summary>
        Friend Sub CopyPixelDataFrom(other As MaskComponent)
            If other Is Nothing Then Return
            _pixels.CopyFrom(other.PixelSlot)
            _brushAdd.CopyFrom(other.BrushAddSlot)
            _brushSubtract.CopyFrom(other.BrushSubtractSlot)
        End Sub

        ''' <summary>Die Bilddaten SO WIE SIE LIEGEN, ohne eine Umwandlung auszuloesen. Nur fuer
        ''' die Uebergabe zwischen Maske und Bestandteil sowie fuer den Renderschluessel.</summary>
        Friend ReadOnly Property PixelSlot As AlphaPixels
            Get
                Return _pixels
            End Get
        End Property

        Friend ReadOnly Property BrushAddSlot As AlphaPixels
            Get
                Return _brushAdd
            End Get
        End Property

        Friend ReadOnly Property BrushSubtractSlot As AlphaPixels
            Get
                Return _brushSubtract
            End Get
        End Property

        Friend ReadOnly Property StoredPng As String
            Get
                Return _pixels.StoredPng
            End Get
        End Property

        Friend ReadOnly Property StoredRaster As AlphaRaster
            Get
                Return _pixels.StoredRaster
            End Get
        End Property

        Friend ReadOnly Property StoredBrushAddPng As String
            Get
                Return _brushAdd.StoredPng
            End Get
        End Property

        Friend ReadOnly Property StoredBrushAddRaster As AlphaRaster
            Get
                Return _brushAdd.StoredRaster
            End Get
        End Property

        Friend ReadOnly Property StoredBrushSubtractPng As String
            Get
                Return _brushSubtract.StoredPng
            End Get
        End Property

        Friend ReadOnly Property StoredBrushSubtractRaster As AlphaRaster
            Get
                Return _brushSubtract.StoredRaster
            End Get
        End Property

        ''' <summary>Trägt diese Maske Bildpunkte? Antwortet OHNE zu packen oder zu entpacken -
        ''' die Frage stellen mehrere Wege sehr oft.</summary>
        <JsonIgnore>
        Public ReadOnly Property HasPixelData As Boolean
            Get
                Return _pixels.HasData
            End Get
        End Property

        Public Property FeatherPixels As Single
        Public Property Inverted As Boolean

        ''' <summary>DICHTE der ganzen Maske in Prozent: wie stark sie überhaupt deckt. 100 = wie
        ''' gemalt, 50 = überall halb, 0 = sie wirkt nicht mehr.
        '''
        ''' Eine Eigenschaft der MASKE und nicht der Ebene, und genau das ist der Punkt: die
        ''' Ebenen-Deckkraft gibt es nur an einer Masken- oder Auswahlebene, eine Ebenenmaske am
        ''' OBJEKT hatte deshalb gar keine Dichte - dort bedeutet die Deckkraft die des Objekts.
        ''' Angewendet wird sie an EINER Stelle, ganz am Ende von BuildPersistentMaskForOutput, wo
        ''' auch die Ebenen-Deckkraft einfliesst; beide multiplizieren sich.
        '''
        ''' Sie gehört in den Fingerabdruck der Maske, sonst gäbe der Zwischenspeicher nach einer
        ''' Änderung das alte Bild zurück - dieselbe Regel wie für jeden Bestandteil.</summary>
        Public Property Density As Double = 100.0

        ''' <summary>Maske vorübergehend AUS: sie bleibt vollständig erhalten, deckt aber überall.
        '''
        ''' Zum Nachsehen, was ohne sie geschähe - der Standardhandgriff beim Aufbauen einer Maske.
        ''' Ausdrücklich NICHT dasselbe wie eine fehlende Maske: die wirkt gar nicht (die Anpassung
        ''' bleibt aus), diese hier lässt die Anpassung überall wirken. Beides gibt es, und beides
        ''' wird gebraucht.</summary>
        Public Property IsDisabled As Boolean

        ''' <summary>Das Auge des ERSTEN Bestandteils. Er liegt in den Feldern der Maske selbst, also
        ''' braucht auch sein Schalter hier seinen Platz - die weiteren tragen ihn selbst
        ''' (MaskComponent.IsVisible).</summary>
        Public Property PrimaryVisible As Boolean = True

        ''' <summary>Umkehrung des FERTIGEN Ergebnisses, nachdem alle Bestandteile verrechnet sind -
        ''' im Unterschied zu <see cref="Inverted"/>, das dem ersten Bestandteil gehört.
        '''
        ''' Nötig, weil sich das Umkehren einer mehrteiligen Maske nicht auf ihre Teile verteilen
        ''' lässt: Hinzufügen und Schneiden sind zwar Gegenstücke (Maximum gegen Minimum), das
        ''' Abziehen ist als geklemmte Differenz aber keines - eine Umkehrung Bestandteil für
        ''' Bestandteil wäre nur bei harten Kanten richtig und bei jedem Zwischenwert falsch.
        ''' Vorher wurde stattdessen die Summe in den ersten Bestandteil geschrieben, während die
        ''' übrigen stehenblieben und ein zweites Mal obendrauf kamen.
        '''
        ''' Angewendet an genau den zwei Stellen, die Bestandteile zusammensetzen: dem Renderweg
        ''' und dem Raster für Miniatur und Bearbeiten. Gehört in den Fingerabdruck, sonst gäbe der
        ''' Zwischenspeicher nach dem Umkehren das alte Bild zurück.</summary>
        Public Property InvertResult As Boolean

        ''' <summary>Optionaler Ursprung einer gerasterten Bereichsmaske. Das Raster bleibt der
        ''' Rendervertrag; die Parameter darunter erlauben es jedoch, Farbe, Luminanz oder Tiefe
        ''' beim erneuten Öffnen der Ebene wieder mit ihren Reglern zu berechnen.</summary>
        Public Property RangeKind As String = ""
        Public Property RangeColorRed As Byte
        Public Property RangeColorGreen As Byte
        Public Property RangeColorBlue As Byte
        Public Property RangeTolerance As Double
        Public Property RangeFeather As Double
        Public Property RangeFrom As Double
        Public Property RangeTo As Double = 100.0
        Public Property RangeSampleXPercent As Double
        Public Property RangeSampleYPercent As Double
        Public Property RangeContiguous As Boolean

        ''' <summary>Art der Maske. Leer = GEMALTE Maske, deren Alphawerte in PngBase64 liegen
        ''' (Rechteck, Ellipse, Lasso, Zauberstab, Masken-Pinsel). "Linear" und "Radial" =
        ''' VERLAUF, der NICHT gebacken wird, sondern bei jedem Render aus seiner Geometrie
        ''' entsteht. Das ist der ganze Unterschied und der Grund fuer den eigenen Typ: ein
        ''' gebackener Verlauf liesse sich hinterher weder drehen noch in der Weichheit aendern,
        ''' ohne die Maske neu zu malen. UMGEKEHRT (aussen statt innen, unten statt oben) ist
        ''' kein eigener Typ, sondern das vorhandene <see cref="Inverted"/>.</summary>
        Public Property Kind As String = ""

        ''' <summary>Geometrie des Verlaufs, in Prozent der QUELLBILD-Maße (wie alle
        ''' Geometrie-Angaben im Rezept).
        ''' LINEAR: Start = volle Deckung, Ende = keine. Der ABSTAND der beiden ist die Weichheit,
        ''' ihre Richtung der Winkel; ausserhalb gilt der jeweilige Endwert, der Verlauf reicht
        ''' also immer ueber das ganze Bild.
        ''' RADIAL: Start = MITTELPUNKT, Ende = ein Punkt auf dem Rand. Der Abstand ist damit der
        ''' Radius und die Richtung die Drehung der Ellipse.</summary>
        Public Property GradientStartXPercent As Double
        Public Property GradientStartYPercent As Double
        Public Property GradientEndXPercent As Double
        Public Property GradientEndYPercent As Double

        ''' <summary>NUR RADIAL: Verhaeltnis der zweiten zur ersten Halbachse. 1 = Kreis, kleiner =
        ''' quer gestaucht, groesser = laengs gestreckt. Damit laesst sich die Flaeche an ein
        ''' Gesicht oder einen Himmelsausschnitt anpassen, ohne einen zweiten Griff zu brauchen.</summary>
        Public Property GradientRadiusRatio As Double = 1.0

        ''' <summary>NUR RADIAL: wie viel des Radius der weiche Uebergang einnimmt, in Prozent.
        ''' 0 = harte Kante, 100 = vom Mittelpunkt an abfallend. Beim linearen Verlauf braucht es
        ''' das nicht - dort IST der Abstand der beiden Punkte die Weichheit.</summary>
        Public Property GradientFeatherPercent As Double = 50.0

        ''' <summary>PINSEL-KORREKTUR eines Verlaufs: zwei Alpha8-Raster im QUELLRAUM, die nach dem
        ''' gerechneten Verlauf verrechnet werden (<c>Deckung = Verlauf + Hinzu - Weg</c>, geklemmt).
        ''' Damit laesst sich ein Verlauf mit dem Maskenpinsel nachbessern - Dach aus dem
        ''' Himmelsverlauf herausnehmen, eine Ecke dazunehmen -, OHNE dass der Verlauf seine
        ''' Aenderbarkeit verliert: seine Geometrie bleibt Geometrie, die Korrektur liegt daneben.
        ''' Deshalb zwei getrennte Raster statt eines vorzeichenbehafteten: beide Richtungen bleiben
        ''' unabhaengig voneinander uebermalbar, und ein leeres Raster kostet nichts.
        '''
        ''' Beide teilen sich EIN Rechteck (die Vereinigung des Bemalten) - getrennte Rechtecke
        ''' waeren zwei weitere Feldergruppen fuer einen Fall, den es praktisch nicht gibt.
        ''' Bei einer GEMALTEN Maske bleiben diese Felder leer; dort verrechnet der Pinsel direkt
        ''' die Maske selbst (siehe WriteSelectionMaskBackToLayer im EditorViewModel).</summary>
        Private ReadOnly _brushAdd As New AlphaPixels()
        Private ReadOnly _brushSubtract As New AlphaPixels()

        ''' <summary>Die Pinselkorrektur als PNG - Speicherform wie <see cref="PngBase64"/>. Beim
        ''' Malen wird das RASTER gesetzt; gepackt wird erst fuer die Datei.</summary>
        Public Property BrushAddPngBase64 As String
            Get
                Return _brushAdd.Png
            End Get
            Set(value As String)
                _brushAdd.Png = value
            End Set
        End Property

        Public Property BrushSubtractPngBase64 As String
            Get
                Return _brushSubtract.Png
            End Get
            Set(value As String)
                _brushSubtract.Png = value
            End Set
        End Property

        <JsonIgnore>
        Public Property BrushAddRaster As AlphaRaster
            Get
                Return _brushAdd.Raster
            End Get
            Set(value As AlphaRaster)
                _brushAdd.Raster = value
            End Set
        End Property

        <JsonIgnore>
        Public Property BrushSubtractRaster As AlphaRaster
            Get
                Return _brushSubtract.Raster
            End Get
            Set(value As AlphaRaster)
                _brushSubtract.Raster = value
            End Set
        End Property

        <JsonIgnore>
        Public ReadOnly Property HasBrushCorrectionData As Boolean
            Get
                Return _brushAdd.HasData OrElse _brushSubtract.HasData
            End Get
        End Property
        Public Property BrushLeft As Integer
        Public Property BrushTop As Integer
        Public Property BrushRight As Integer
        Public Property BrushBottom As Integer

        ''' <summary>True, wenn ein Verlauf eine Pinselkorrektur traegt.</summary>
        Public ReadOnly Property HasBrushCorrection As Boolean
            Get
                Return BrushRight > BrushLeft AndAlso BrushBottom > BrushTop AndAlso HasBrushCorrectionData
            End Get
        End Property

        ''' <summary>True fuer beide Verlaufsarten - sie teilen sich Speicherung, Renderweg und
        ''' Cache-Schluessel.</summary>
        Public ReadOnly Property IsGradient As Boolean
            Get
                Return IsLinearGradient OrElse IsRadialGradient
            End Get
        End Property

        Public ReadOnly Property IsLinearGradient As Boolean
            Get
                Return String.Equals(Kind, "Linear", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsRadialGradient As Boolean
            Get
                Return String.Equals(Kind, "Radial", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' <summary>WEITERE Bestandteile dieser Maske, nach dem ersten. Jeder trägt seine eigene Art,
        ''' Geometrie, Weichheit und Umkehrung und wird mit seinem <see cref="MaskComponent.Mode"/> auf
        ''' das bisherige Ergebnis verrechnet. Leer = die Maske besteht aus genau einem Bestandteil,
        ''' und das ist der Normalfall.
        '''
        ''' WARUM der erste Bestandteil nicht mit in dieser Liste steht: er liegt in den Feldern
        ''' oberhalb, und zwar unverändert. Damit lesen und schreiben rund hundert vorhandene Stellen
        ''' im Editor weiterhin genau das, was sie meinen - „den Bestandteil, an dem gerade gearbeitet
        ''' wird" -, und eine gespeicherte Bearbeitung von früher öffnet ohne Wanderung. Gelesen wird
        ''' trotzdem einheitlich, nämlich ausschließlich über <see cref="GetComponents"/>.</summary>
        Public Property ExtraComponents As New System.Collections.Generic.List(Of MaskComponent)()

        ''' <summary>Der erste Bestandteil als eigenständiges Objekt - eine ABSCHRIFT der Felder
        ''' oberhalb, kein Verweis darauf. Wer ihn ändern will, ändert die Felder.</summary>
        Public Function PrimaryAsComponent() As MaskComponent
            Dim c = New MaskComponent With {
                .Mode = "Add",
                .IsVisible = PrimaryVisible,
                .Kind = Kind,
                .Left = Left, .Top = Top, .Right = Right, .Bottom = Bottom,
                .FeatherPixels = FeatherPixels, .Inverted = Inverted,
                .GradientStartXPercent = GradientStartXPercent, .GradientStartYPercent = GradientStartYPercent,
                .GradientEndXPercent = GradientEndXPercent, .GradientEndYPercent = GradientEndYPercent,
                .GradientRadiusRatio = GradientRadiusRatio, .GradientFeatherPercent = GradientFeatherPercent,
                .BrushLeft = BrushLeft, .BrushTop = BrushTop, .BrushRight = BrushRight, .BrushBottom = BrushBottom}
            ' NICHT ueber .PngBase64 im Erzeuger oben: das packte die Maske bei JEDER Zerlegung.
            c.CopyPixelDataFrom(Me)
            Return c
        End Function

        ''' <summary>Schreibt einen Bestandteil in die Felder des ERSTEN zurück.</summary>
        Public Sub SetPrimaryFromComponent(c As MaskComponent)
            If c Is Nothing Then Return
            PrimaryVisible = c.IsVisible
            Kind = c.Kind
            Left = c.Left : Top = c.Top : Right = c.Right : Bottom = c.Bottom
            CopyPixelDataFrom(c) : FeatherPixels = c.FeatherPixels : Inverted = c.Inverted
            GradientStartXPercent = c.GradientStartXPercent : GradientStartYPercent = c.GradientStartYPercent
            GradientEndXPercent = c.GradientEndXPercent : GradientEndYPercent = c.GradientEndYPercent
            GradientRadiusRatio = c.GradientRadiusRatio : GradientFeatherPercent = c.GradientFeatherPercent
            BrushLeft = c.BrushLeft : BrushTop = c.BrushTop : BrushRight = c.BrushRight : BrushBottom = c.BrushBottom
        End Sub

        ''' <summary>Trägt der erste Bestandteil überhaupt etwas? Eine frisch angelegte Maske ist
        ''' leer, und dann gehört der erste hinzugefügte Bestandteil dorthin statt in die Liste.</summary>
        Public ReadOnly Property HasPrimaryComponent As Boolean
            Get
                If IsGradient Then Return True
                ' HasPixelData und NICHT die Speicherform: diese Frage stellt GetComponents bei
                ' jedem Rastern, und ein Blick auf PngBase64 packt die Maske dabei jedesmal neu.
                Return Right > Left AndAlso Bottom > Top AndAlso HasPixelData
            End Get
        End Property

        ''' <summary>ALLE Bestandteile in Wirkreihenfolge. Die EINE Stelle, über die gelesen wird.</summary>
        Public Function GetComponents() As System.Collections.Generic.List(Of MaskComponent)
            Dim list As New System.Collections.Generic.List(Of MaskComponent)()
            If HasPrimaryComponent Then list.Add(PrimaryAsComponent())
            If ExtraComponents IsNot Nothing Then
                For Each c In ExtraComponents
                    If c IsNot Nothing Then list.Add(c)
                Next
            End If
            Return list
        End Function

        Public ReadOnly Property ComponentCount As Integer
            Get
                Return GetComponents().Count
            End Get
        End Property

        ''' <summary>Hängt einen Bestandteil an. Ist der erste noch leer, wird er DORT abgelegt - eine
        ''' Maske, deren einziger Bestandteil in der Zusatzliste stünde, wäre für jede vorhandene
        ''' Stelle im Editor eine leere Maske.</summary>
        Public Sub AddComponent(c As MaskComponent)
            If c Is Nothing Then Return
            If Not HasPrimaryComponent Then
                SetPrimaryFromComponent(c)
                Return
            End If
            If ExtraComponents Is Nothing Then ExtraComponents = New System.Collections.Generic.List(Of MaskComponent)()
            ExtraComponents.Add(c)
        End Sub

        ''' <summary>Entfernt den Bestandteil an dieser Stelle. Faellt der ERSTE weg, rueckt der
        ''' naechste in seine Felder nach - sonst stuende die Maske mit leerem ersten Bestandteil da
        ''' und gaelte ueberall als leer.</summary>
        Public Sub RemoveComponentAt(index As Integer)
            Dim list = GetComponents()
            If index < 0 OrElse index >= list.Count Then Return
            list.RemoveAt(index)
            If list.Count = 0 Then
                SetPrimaryFromComponent(New MaskComponent())
                ExtraComponents = New System.Collections.Generic.List(Of MaskComponent)()
                Return
            End If
            SetPrimaryFromComponent(list(0))
            ExtraComponents = list.Skip(1).ToList()
        End Sub

        Public Function Clone() As ImageMask
            Dim copy = New ImageMask With {
                .Id = Id, .Name = Name,
                .SourceWidthPixels = SourceWidthPixels, .SourceHeightPixels = SourceHeightPixels,
                .Left = Left, .Top = Top, .Right = Right, .Bottom = Bottom,
                .FeatherPixels = FeatherPixels, .Inverted = Inverted,
                .Density = Density, .IsDisabled = IsDisabled, .PrimaryVisible = PrimaryVisible,
                .InvertResult = InvertResult,
                .RangeKind = RangeKind, .RangeColorRed = RangeColorRed, .RangeColorGreen = RangeColorGreen,
                .RangeColorBlue = RangeColorBlue, .RangeTolerance = RangeTolerance,
                .RangeFeather = RangeFeather, .RangeFrom = RangeFrom, .RangeTo = RangeTo,
                .RangeSampleXPercent = RangeSampleXPercent, .RangeSampleYPercent = RangeSampleYPercent,
                .RangeContiguous = RangeContiguous,
                .Kind = Kind,
                .GradientStartXPercent = GradientStartXPercent, .GradientStartYPercent = GradientStartYPercent,
                .GradientEndXPercent = GradientEndXPercent, .GradientEndYPercent = GradientEndYPercent,
                .GradientRadiusRatio = GradientRadiusRatio, .GradientFeatherPercent = GradientFeatherPercent,
                .BrushLeft = BrushLeft, .BrushTop = BrushTop, .BrushRight = BrushRight, .BrushBottom = BrushBottom,
                .ExtraComponents = If(ExtraComponents Is Nothing,
                                      New System.Collections.Generic.List(Of MaskComponent)(),
                                      ExtraComponents.Where(Function(c) c IsNot Nothing).Select(Function(c) c.Clone()).ToList())
            }
            copy.CopyPixelDataFrom(Me)
            Return copy
        End Function
    End Class

    ''' <summary>EIN Bestandteil einer Maske: entweder ein gemaltes Raster oder ein gerechneter
    ''' Verlauf, verrechnet mit <see cref="Mode"/> auf das Ergebnis der Bestandteile davor.
    '''
    ''' Die Felder sind absichtlich dieselben wie die der <see cref="ImageMask"/> - der erste
    ''' Bestandteil LIEGT dort, und beide Seiten dürfen nicht auseinanderlaufen.</summary>
    Public Class MaskComponent
        ''' <summary>„Add", „Subtract" oder „Intersect". Der ERSTE Bestandteil setzt das Ergebnis
        ''' unabhängig von seinem Modus - es gibt vor ihm nichts, worauf man rechnen könnte.</summary>
        Public Property Mode As String = "Add"

        Public Property Kind As String = ""
        Public Property Left As Integer
        Public Property Top As Integer
        Public Property Right As Integer
        Public Property Bottom As Integer

        Private ReadOnly _pixels As New AlphaPixels()

        ''' <summary>Speicherform, siehe <see cref="ImageMask.PngBase64"/>.</summary>
        Public Property PngBase64 As String
            Get
                Return _pixels.Png
            End Get
            Set(value As String)
                _pixels.Png = value
            End Set
        End Property

        ''' <summary>Arbeitsform, siehe <see cref="ImageMask.Raster"/>.</summary>
        <JsonIgnore>
        Public Property Raster As AlphaRaster
            Get
                Return _pixels.Raster
            End Get
            Set(value As AlphaRaster)
                _pixels.Raster = value
            End Set
        End Property

        Friend Sub CopyPixelDataFrom(other As MaskComponent)
            If other Is Nothing Then Return
            _pixels.CopyFrom(other._pixels)
            _brushAdd.CopyFrom(other._brushAdd)
            _brushSubtract.CopyFrom(other._brushSubtract)
        End Sub

        Friend Sub CopyPixelDataFrom(other As ImageMask)
            If other Is Nothing Then Return
            _pixels.CopyFrom(other.PixelSlot)
            _brushAdd.CopyFrom(other.BrushAddSlot)
            _brushSubtract.CopyFrom(other.BrushSubtractSlot)
        End Sub

        ''' <summary>Die Bilddaten SO WIE SIE LIEGEN, ohne eine Umwandlung auszuloesen. Nur fuer
        ''' die Uebergabe zwischen Maske und Bestandteil sowie fuer den Renderschluessel.</summary>
        Friend ReadOnly Property PixelSlot As AlphaPixels
            Get
                Return _pixels
            End Get
        End Property

        Friend ReadOnly Property BrushAddSlot As AlphaPixels
            Get
                Return _brushAdd
            End Get
        End Property

        Friend ReadOnly Property BrushSubtractSlot As AlphaPixels
            Get
                Return _brushSubtract
            End Get
        End Property

        Friend ReadOnly Property StoredPng As String
            Get
                Return _pixels.StoredPng
            End Get
        End Property

        Friend ReadOnly Property StoredRaster As AlphaRaster
            Get
                Return _pixels.StoredRaster
            End Get
        End Property

        Friend ReadOnly Property StoredBrushAddPng As String
            Get
                Return _brushAdd.StoredPng
            End Get
        End Property

        Friend ReadOnly Property StoredBrushAddRaster As AlphaRaster
            Get
                Return _brushAdd.StoredRaster
            End Get
        End Property

        Friend ReadOnly Property StoredBrushSubtractPng As String
            Get
                Return _brushSubtract.StoredPng
            End Get
        End Property

        Friend ReadOnly Property StoredBrushSubtractRaster As AlphaRaster
            Get
                Return _brushSubtract.StoredRaster
            End Get
        End Property

        ''' <summary>Trägt dieser Bestandteil Bildpunkte? Antwortet OHNE zu packen oder zu
        ''' entpacken - die Frage stellen mehrere Wege sehr oft.</summary>
        <JsonIgnore>
        Public ReadOnly Property HasPixelData As Boolean
            Get
                Return _pixels.HasData
            End Get
        End Property

        Public Property FeatherPixels As Single
        Public Property Inverted As Boolean
        Public Property GradientStartXPercent As Double
        Public Property GradientStartYPercent As Double
        Public Property GradientEndXPercent As Double
        Public Property GradientEndYPercent As Double
        Public Property GradientRadiusRatio As Double = 1.0
        Public Property GradientFeatherPercent As Double = 50.0
        Private ReadOnly _brushAdd As New AlphaPixels()
        Private ReadOnly _brushSubtract As New AlphaPixels()

        ''' <summary>Speicherform der Pinselkorrektur, siehe <see cref="ImageMask.BrushAddPngBase64"/>.</summary>
        Public Property BrushAddPngBase64 As String
            Get
                Return _brushAdd.Png
            End Get
            Set(value As String)
                _brushAdd.Png = value
            End Set
        End Property

        Public Property BrushSubtractPngBase64 As String
            Get
                Return _brushSubtract.Png
            End Get
            Set(value As String)
                _brushSubtract.Png = value
            End Set
        End Property

        <JsonIgnore>
        Public Property BrushAddRaster As AlphaRaster
            Get
                Return _brushAdd.Raster
            End Get
            Set(value As AlphaRaster)
                _brushAdd.Raster = value
            End Set
        End Property

        <JsonIgnore>
        Public Property BrushSubtractRaster As AlphaRaster
            Get
                Return _brushSubtract.Raster
            End Get
            Set(value As AlphaRaster)
                _brushSubtract.Raster = value
            End Set
        End Property

        <JsonIgnore>
        Public ReadOnly Property HasBrushCorrectionData As Boolean
            Get
                Return _brushAdd.HasData OrElse _brushSubtract.HasData
            End Get
        End Property
        Public Property BrushLeft As Integer
        Public Property BrushTop As Integer
        Public Property BrushRight As Integer
        Public Property BrushBottom As Integer

        ''' <summary>Zählt dieser Bestandteil beim Zusammensetzen mit? Ausgeschaltet bleibt er
        ''' erhalten und wird nur übersprungen - zum Nachsehen, was er beiträgt.
        '''
        ''' Er wird an DERSELBEN Stelle übersprungen, an der ein leerer Bestandteil wegfällt, und
        ''' die Regel "der ERSTE setzt das Ergebnis" gilt danach für den ersten SICHTBAREN. Alles
        ''' andere wäre schwer zu erklären: eine ausgeschaltete Grundform, auf die noch abgezogen
        ''' wird, ergäbe eine leere Maske.</summary>
        Public Property IsVisible As Boolean = True

        Public ReadOnly Property IsLinearGradient As Boolean
            Get
                Return String.Equals(Kind, "Linear", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsRadialGradient As Boolean
            Get
                Return String.Equals(Kind, "Radial", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsGradient As Boolean
            Get
                Return IsLinearGradient OrElse IsRadialGradient
            End Get
        End Property

        Public ReadOnly Property HasBrushCorrection As Boolean
            Get
                Return BrushRight > BrushLeft AndAlso BrushBottom > BrushTop AndAlso HasBrushCorrectionData
            End Get
        End Property

        Public Function Clone() As MaskComponent
            Dim copy = New MaskComponent With {
                .Mode = Mode, .Kind = Kind, .IsVisible = IsVisible,
                .Left = Left, .Top = Top, .Right = Right, .Bottom = Bottom,
                .FeatherPixels = FeatherPixels, .Inverted = Inverted,
                .GradientStartXPercent = GradientStartXPercent, .GradientStartYPercent = GradientStartYPercent,
                .GradientEndXPercent = GradientEndXPercent, .GradientEndYPercent = GradientEndYPercent,
                .GradientRadiusRatio = GradientRadiusRatio, .GradientFeatherPercent = GradientFeatherPercent,
                .BrushLeft = BrushLeft, .BrushTop = BrushTop, .BrushRight = BrushRight, .BrushBottom = BrushBottom}
            copy.CopyPixelDataFrom(Me)
            Return copy
        End Function
    End Class

    ''' <summary>Eine MASKEN- oder AUSWAHLEBENE: eine Maske (über MaskId) und die Regler, die nur
    ''' innerhalb dieser Maske wirken. Welche der beiden Arten es ist, sagt IsMaskLayer - der
    ''' Unterschied ist die Darstellung (rot gegen Laufameisen), nicht der Aufbau.
    '''
    ''' Der Klassenname trägt noch das Wort "Adjustment"; in der Oberfläche kommt es NICHT vor
    ''' (Patrick am 2026-08-06: "was das mit der Korrektur soll ist mir auch unklar"). Dort gibt es
    ''' Maskenebene, Auswahlebene, Ebene mit Maske und Bestandteil, mehr nicht.</summary>
    Public Class MaskedAdjustmentLayer
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = LocalizationService.T("Maskenebene")
        Public Property MaskId As String = ""
        Public Property IsVisible As Boolean = True
        ''' <summary>GESPERRT - siehe ImageAnnotation.IsLocked. Bei einer Korrekturebene heißt das:
        ''' ihre Maske wandert bei Gruppen-Transformationen nicht mit.</summary>
        Public Property IsLocked As Boolean = False
        Public Property Opacity As Single = 1.0F
        Public Property Adjustments As ImageAdjustments = New ImageAdjustments()
        ''' <summary>True = diese Ebene wurde aus einem XMP-Preset importiert (lokale Korrektur).
        ''' Beim Anwenden eines ANDEREN Presets werden diese entfernt, damit sich Presets nicht
        ''' aufeinander stapeln - manuell erstellte Ebenen (False) bleiben davon unberührt.</summary>
        Public Property FromPreset As Boolean = False
        ''' <summary>Thematische Art der Ebene: False = AUSWAHL-Ebene (aus Rechteck/Ellipse/Lasso/
        ''' Zauberstab, im Editor als Laufameisen), True = MASKEN-Ebene (aus Masken-Pinsel/Verlauf, im
        ''' Editor als rotes Overlay). Steuert Symbol/Name im Ebenen-Panel und die Overlay-Darstellung.</summary>
        Public Property IsMaskLayer As Boolean = False

        ''' <summary>Zugehörigkeit zu einer Gruppe (leer = keine). Dieselben Gruppen wie bei den
        ''' Objekt-Ebenen (<see cref="AnnotationGroup"/>), aber eine Gruppe fasst entweder Objekte ODER
        ''' Korrekturebenen zusammen: beide sind getrennte Rendergruppen (lokale Korrekturen laufen VOR
        ''' den Objekt-Overlays), eine gemischte Gruppe würde eine Reihenfolge vortäuschen, die es im
        ''' Renderer nicht gibt.</summary>
        Public Property GroupId As String = ""

        ''' <summary>Leer = die Korrektur liegt im BASISBILD (schnell, im Basis-Cache) und wirkt damit
        ''' unter allen Objekten - das ist der Normalfall. Steht hier die Id eines Objekts, ist die
        ''' Korrektur ÜBER dieses Objekt einsortiert und wirkt auf alles, was darunter liegt: Basis
        ''' UND die bis dahin gezeichneten Objekte. Solche Ebenen fallen aus dem Basis-Cache heraus,
        ''' sie müssen bei jedem Render auf das Komposit angewendet werden.</summary>
        Public Property StackAboveAnnotationId As String = ""

        ''' <summary>AUF DIE EBENE DARUNTER BESCHRÄNKT: die Korrektur wirkt nur dort, wo das Objekt
        ''' deckt, über dem sie einsortiert ist - dieselbe Schnittmaske, die es an einem OBJEKT schon
        ''' gibt (<see cref="ImageAnnotation.ClipToLayerBelow"/>).
        '''
        ''' Sie braucht dafür einen Anker: ohne <see cref="StackAboveAnnotationId"/> liegt die
        ''' Korrektur im Basisbild, UNTER allen Objekten, und es gibt keine Ebene darunter, auf die
        ''' sich etwas beschränken liesse. Der Schalter bleibt dort ohne Wirkung, statt heimlich
        ''' etwas anderes zu bedeuten.</summary>
        Public Property ClipToLayerBelow As Boolean = False

        ''' <summary>DEKLARATIVE Füllung (kein PNG/Objekt): leer = keine Füllung, sonst "Solid"/
        ''' "LinearGradient"/"RadialGradient". Bei einer AUSWAHL-Ebene wird die Füllung SICHTBAR in die
        ''' Auswahl komponiert (Farbe/Verlauf); bei einer MASKEN-Ebene stuft die LUMINANZ der Füllung die
        ''' Maske ab und bestimmt so, wie stark die Anpassung der Ebene je Bereich wirkt. Der Render zeichnet
        ''' beides selbst - so bleibt die Füllung nachträglich änderbar (Winkel/Farben) statt eingebrannt.</summary>
        Public Property FillKind As String = ""
        Public Property FillColor As String = "#FFFFFFFF"
        Public Property FillColor2 As String = "#FF000000"
        Public Property FillAngle As Double = 0
        Public Property FillInverted As Boolean = False

        Public Function Clone() As MaskedAdjustmentLayer
            Return New MaskedAdjustmentLayer With {
                .Id = Id, .Name = Name, .MaskId = MaskId,
                .IsVisible = IsVisible, .IsLocked = IsLocked, .Opacity = Opacity, .FromPreset = FromPreset, .IsMaskLayer = IsMaskLayer,
                .GroupId = GroupId,
                .StackAboveAnnotationId = StackAboveAnnotationId,
                .ClipToLayerBelow = ClipToLayerBelow,
                .FillKind = FillKind, .FillColor = FillColor, .FillColor2 = FillColor2,
                .FillAngle = FillAngle, .FillInverted = FillInverted,
                .Adjustments = If(Adjustments Is Nothing, New ImageAdjustments(), Adjustments.Clone())
            }
        End Function

        ''' <summary>True, wenn diese Ebene eine deklarative Füllung trägt.</summary>
        Public Function HasFill() As Boolean
            Return Not String.IsNullOrWhiteSpace(FillKind)
        End Function
    End Class

End Namespace
