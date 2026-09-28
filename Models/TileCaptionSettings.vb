Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports FerrumPix.Services

Namespace Models

    ''' <summary>Was unter einer Galeriekachel und in einer Zeile der Liste stehen kann. Die Werte
    ''' kommen aus dem Katalog bzw. aus dem, was das Element ohnehin weiss; nichts davon liest die
    ''' Datei. Gespeichert wird der NAME, nicht die Zahl - die Reihenfolge hier darf sich also
    ''' aendern.</summary>
    Public Enum TileCaptionField
        None = 0
        DateTaken
        FileModified
        FileCreated
        Dimensions
        Megapixels
        FileSize
        FileType
        Camera
        Lens
        FocalLength
        Aperture
        ShutterSpeed
        Iso
        Place
    End Enum

    ''' <summary>
    ''' Die Wahl fuer die Angaben unter einer Kachel: bis zu drei Zeilen, jede mit einer Angabe am
    ''' linken und einer am rechten Rand. Eine Zeile, in der beide leer sind, gibt es nicht - weder
    ''' unter der Kachel noch in der Liste. Anwendungsweit und statisch aus demselben Grund wie bei
    ''' <c>InfoPanelRowSettings</c>: jede Kachel fragt die Wahl bei jeder Bindung ab, und ein
    ''' Deserialisieren der Einstellungsdatei je Kachel waere ein Vielfaches des Zeichnens.
    '''
    ''' <para>Gespeichert als EINE Zeichenkette je Seite, die Zeilen durch Kommas getrennt und eine
    ''' leere Angabe als "None". Eine leere Zeichenkette heisst also "nichts auf dieser Seite" und
    ''' ist von "noch nie gespeichert" zu unterscheiden: das zweite ist das fehlende Feld, und das
    ''' faellt auf den Werkswert in <c>AppSettings</c>.</para>
    '''
    ''' <para>Ob eine Zeile steht, haengt NUR an der Wahl, nie am einzelnen Bild. Die Kacheln des
    ''' Rasters sind alle gleich hoch (UniformGridLayout misst eine und nimmt ihr Mass fuer alle);
    ''' fiele eine Zeile weg, weil einem Bild der Wert fehlt, stuenden unterschiedlich hohe Kacheln
    ''' im Raster.</para>
    ''' </summary>
    Public NotInheritable Class TileCaptionSettings

        Private Sub New()
        End Sub

        ''' <summary>So viele Zeilen gibt es hoechstens, also sechs Angaben.</summary>
        Public Const RowCount As Integer = 3

        Public Const DefaultLeft As String = "DateTaken,None,None"
        Public Const DefaultRight As String = "FileSize,None,None"

        ''' <summary>Die waehlbaren Angaben in der Reihenfolge der Auswahlliste. Die Daten stehen
        ''' vorn, dann Bild und Datei, dann die Aufnahme, der Ort zuletzt.</summary>
        Public Shared ReadOnly Property AllFields As IReadOnlyList(Of TileCaptionField) =
            New TileCaptionField() {TileCaptionField.None,
                                    TileCaptionField.DateTaken, TileCaptionField.FileModified,
                                    TileCaptionField.FileCreated, TileCaptionField.Dimensions,
                                    TileCaptionField.Megapixels, TileCaptionField.FileSize,
                                    TileCaptionField.FileType, TileCaptionField.Camera,
                                    TileCaptionField.Lens, TileCaptionField.FocalLength,
                                    TileCaptionField.Aperture, TileCaptionField.ShutterSpeed,
                                    TileCaptionField.Iso, TileCaptionField.Place}

        Private Shared _left As TileCaptionField() = Parse(DefaultLeft)
        Private Shared _right As TileCaptionField() = Parse(DefaultRight)

        ''' <summary>Meldet eine geaenderte Wahl. Die Galerie hoert darauf, laesst ihre Kacheln
        ''' neu fragen und rechnet die Kachelhoehe neu; ohne das stuende die alte Zeile bis zum
        ''' naechsten Ordnerwechsel.</summary>
        Public Shared Event Changed()

        Shared Sub New()
            Dim settings = AppSettingsService.Load()
            If settings Is Nothing Then Return
            _left = Parse(settings.GalleryTileCaptionLeft)
            _right = Parse(settings.GalleryTileCaptionRight)
        End Sub

        Public Shared Function LeftField(row As Integer) As TileCaptionField
            Return If(row >= 0 AndAlso row < _left.Length, _left(row), TileCaptionField.None)
        End Function

        Public Shared Function RightField(row As Integer) As TileCaptionField
            Return If(row >= 0 AndAlso row < _right.Length, _right(row), TileCaptionField.None)
        End Function

        ''' <summary>Steht diese Zeile? Nur wenn auf wenigstens einer Seite etwas gewaehlt ist.</summary>
        Public Shared Function IsRowUsed(row As Integer) As Boolean
            Return LeftField(row) <> TileCaptionField.None OrElse RightField(row) <> TileCaptionField.None
        End Function

        ''' <summary>Wie viele Zeilen stehen. Geht in die geschaetzte Kachelhoehe ein.</summary>
        Public Shared ReadOnly Property UsedRowCount As Integer
            Get
                Return Enumerable.Range(0, RowCount).Count(Function(r) IsRowUsed(r))
            End Get
        End Property

        ''' <summary>Uebernimmt eine Wahl aus dem Einstellungsdialog. Gespeichert wird dort; hier
        ''' steht nur der Stand, nach dem Kacheln und Liste zeichnen.</summary>
        Public Shared Sub Apply(left As String, right As String)
            Dim newLeft = Parse(left)
            Dim newRight = Parse(right)
            If newLeft.SequenceEqual(_left) AndAlso newRight.SequenceEqual(_right) Then Return
            _left = newLeft
            _right = newRight
            RaiseEvent Changed()
        End Sub

        ''' <summary>Liest eine gespeicherte Seite. Unbekannte Namen (aus einer neueren Fassung,
        ''' oder von Hand verschrieben) werden zur leeren Angabe statt die ganze Seite zu verwerfen;
        ''' fehlende Zeilen am Ende ebenso.</summary>
        Public Shared Function Parse(value As String) As TileCaptionField()
            Dim result = New TileCaptionField(RowCount - 1) {}
            Dim parts = If(value, "").Split(","c)
            For i = 0 To Math.Min(parts.Length, RowCount) - 1
                Dim field As TileCaptionField
                If [Enum].TryParse(parts(i).Trim(), ignoreCase:=False, result:=field) AndAlso
                   [Enum].IsDefined(field) Then
                    result(i) = field
                End If
            Next
            Return result
        End Function

        ''' <summary>Die Gegenrichtung zu <see cref="Parse"/>: immer alle Zeilen, damit die Lage
        ''' einer Angabe erhalten bleibt, auch wenn eine Zeile davor leer ist.</summary>
        Public Shared Function Format(fields As IEnumerable(Of TileCaptionField)) As String
            Dim list = If(fields, Enumerable.Empty(Of TileCaptionField)()).Take(RowCount).ToList()
            While list.Count < RowCount
                list.Add(TileCaptionField.None)
            End While
            Return String.Join(",", list.Select(Function(f) f.ToString()))
        End Function

    End Class

End Namespace
