Imports System
Imports System.Runtime.InteropServices
Imports System.Threading
Imports SkiaSharp

' Das Alpha-Raster im Speicher: die Form, in der Masken und Auswahl INNERHALB des Programms
' vorliegen. PNG entsteht erst an der Grenze zur Datei.
'
' WARUM ES DAS GIBT: Masken und Auswahl fuehrten ihre Bildpunkte als PNG in einer
' Base64-Zeichenkette. Zum Rechnen muss das aber ein Raster sein, also wurde bei jeder Aenderung
' dekodiert, gerechnet und wieder kodiert - auch dort, wo die Daten das Programm gar nicht
' verlassen. Gemessen an einem 50-MP-Raster: kodieren 365 ms, dekodieren 92 ms, den rohen Puffer
' kopieren 6 ms. Beim Malen faellt das je Strich an.
Namespace Services

    ''' <summary>Ein Alpha-Raster: ein Byte Deckung je Bildpunkt, Zeilensprung = Breite.
    '''
    ''' UNVERAENDERLICH. Wer etwas anderes braucht, baut ein neues - nur so darf ein Raster
    ''' zwischen Maske, Abschrift und Renderfaden geteilt werden, ohne dass jemand einem anderen
    ''' unter den Fuessen wegzieht. Aus demselben Grund ist der Fingerabdruck ueber den Inhalt
    ''' bestimmt und danach fest.</summary>
    Public NotInheritable Class AlphaRaster
        ''' <summary>Die Packstufe fuer PNG. Nur Zeit und Groesse haengen daran, nichts am Bild:
        ''' PNG ist verlustlos. Sie steht hier, damit alle Wege dieselbe nehmen.</summary>
        Public Const PngCompressionQuality As Integer = 60

        Private ReadOnly _pixels As Byte()
        Private _fingerprint As String
        Private Shared _packCount As Long

        ''' <summary>Wie oft in diesem Lauf ueberhaupt gepackt wurde. Fuer die Diagnose: sie haelt
        ''' damit fest, dass beim ARBEITEN mit Masken keine Speicherform entsteht - genau darum ging
        ''' es beim Umbau. Ein Zaehler statt einer Vermutung: wer irgendwo wieder ueber die
        ''' Speicherform liest, faellt auf, ohne dass jemand die Stelle erraten muss.</summary>
        Public Shared ReadOnly Property PackCount As Long
            Get
                Return Interlocked.Read(_packCount)
            End Get
        End Property

        Public ReadOnly Property Width As Integer
        Public ReadOnly Property Height As Integer

        ''' <summary>Die Bildpunkte, Zeile fuer Zeile ohne Luecke. NICHT hineinschreiben.</summary>
        Public ReadOnly Property Pixels As Byte()
            Get
                Return _pixels
            End Get
        End Property

        Public Sub New(width As Integer, height As Integer, pixels As Byte())
            If width <= 0 OrElse height <= 0 Then Throw New ArgumentOutOfRangeException(NameOf(width))
            If pixels Is Nothing OrElse pixels.Length < width * height Then Throw New ArgumentException("Puffer zu klein", NameOf(pixels))
            Me.Width = width
            Me.Height = height
            _pixels = pixels
        End Sub

        ''' <summary>Aus einem Alpha8-Bild. Der Zeilensprung des Bildes kann groesser sein als seine
        ''' Breite; hier wird dicht gepackt.</summary>
        Public Shared Function FromBitmap(bitmap As SKBitmap) As AlphaRaster
            If bitmap Is Nothing OrElse bitmap.Width <= 0 OrElse bitmap.Height <= 0 Then Return Nothing
            If bitmap.ColorType <> SKColorType.Alpha8 Then Return Nothing
            Dim width = bitmap.Width, height = bitmap.Height
            Dim stride = bitmap.RowBytes
            Dim source = New Byte(stride * height - 1) {}
            Marshal.Copy(bitmap.GetPixels(), source, 0, source.Length)
            If stride = width Then Return New AlphaRaster(width, height, source)
            Dim dense = New Byte(width * height - 1) {}
            For y = 0 To height - 1
                Buffer.BlockCopy(source, y * stride, dense, y * width, width)
            Next
            Return New AlphaRaster(width, height, dense)
        End Function

        ''' <summary>Aus einem gespeicherten PNG. Nothing, wenn es keins ist oder nicht Alpha8 -
        ''' der Aufrufer nimmt dann seinen langen Weg, nie eine falsch belegte Maske.</summary>
        Public Shared Function FromPngBase64(base64 As String) As AlphaRaster
            If String.IsNullOrWhiteSpace(base64) Then Return Nothing
            Try
                Using decoded = SKBitmap.Decode(Convert.FromBase64String(base64))
                    Return FromBitmap(decoded)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Ein Alpha8-Bild aus diesem Raster - fuer die Wege, die eine SKBitmap brauchen.
        ''' Der Aufrufer entsorgt sie.</summary>
        Public Function ToBitmap() As SKBitmap
            Dim bitmap = New SKBitmap(Width, Height, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim stride = bitmap.RowBytes
            If stride = Width Then
                Marshal.Copy(_pixels, 0, bitmap.GetPixels(), Width * Height)
                Return bitmap
            End If
            Dim padded = New Byte(stride * Height - 1) {}
            For y = 0 To Height - 1
                Buffer.BlockCopy(_pixels, y * Width, padded, y * stride, Width)
            Next
            Marshal.Copy(padded, 0, bitmap.GetPixels(), padded.Length)
            Return bitmap
        End Function

        ''' <summary>Als PNG fuer die Datei. Nur hier wird gepackt.</summary>
        Public Function ToPngBase64() As String
            Interlocked.Increment(_packCount)
            Try
                Using bitmap = ToBitmap()
                    Using image = SKImage.FromBitmap(bitmap)
                        Using data = image.Encode(SKEncodedImageFormat.Png, PngCompressionQuality)
                            Return Convert.ToBase64String(data.ToArray())
                        End Using
                    End Using
                End Using
            Catch
                Return ""
            End Try
        End Function

        ''' <summary>Deckt dieses Raster ueberhaupt etwas?</summary>
        Public Function HasCoverage() As Boolean
            For i = 0 To Width * Height - 1
                If _pixels(i) <> 0 Then Return True
            Next
            Return False
        End Function

        ''' <summary>Die engsten Grenzen um alles Gedeckte, als halboffenes Rechteck. Breite oder
        ''' Hoehe 0 heisst: nichts gedeckt.</summary>
        Public Function CoverageBounds() As SKRectI
            Dim left = Width, top = Height, right = 0, bottom = 0
            For y = 0 To Height - 1
                Dim row = y * Width
                For x = 0 To Width - 1
                    If _pixels(row + x) = 0 Then Continue For
                    If x < left Then left = x
                    If x >= right Then right = x + 1
                    If y < top Then top = y
                    bottom = y + 1
                Next
            Next
            If right <= left OrElse bottom <= top Then Return SKRectI.Empty
            Return New SKRectI(left, top, right, bottom)
        End Function

        ''' <summary>Ein Ausschnitt als eigenes Raster. Das Rechteck ist halboffen und muss
        ''' innerhalb liegen.</summary>
        Public Function Crop(rect As SKRectI) As AlphaRaster
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing
            If rect.Left < 0 OrElse rect.Top < 0 OrElse rect.Right > Width OrElse rect.Bottom > Height Then Return Nothing
            If rect.Left = 0 AndAlso rect.Top = 0 AndAlso rect.Width = Width AndAlso rect.Height = Height Then Return Me
            Dim cut = New Byte(rect.Width * rect.Height - 1) {}
            For y = 0 To rect.Height - 1
                Buffer.BlockCopy(_pixels, (rect.Top + y) * Width + rect.Left, cut, y * rect.Width, rect.Width)
            Next
            Return New AlphaRaster(rect.Width, rect.Height, cut)
        End Function

        ''' <summary>Der Fingerabdruck des INHALTS: gleiche Bildpunkte, gleiche Zeichenkette. Er
        ''' beantwortet „ist das dieselbe Maske?" und geht in die Cache-Schluessel ein.
        '''
        ''' WARUM NICHT DAS PNG DAFUER: die Bytes eines PNG haengen auch am Kodierer und an seiner
        ''' Packstufe. Dasselbe Bild ueber zwei Wege ergab damit zwei Zeichenketten, und der
        ''' Vergleich sagte „verschieden", obwohl kein Bildpunkt anders war.
        '''
        ''' WARUM KEIN SHA: der laeuft ueber 50 MB rohe Bildpunkte einige hundert Millisekunden,
        ''' und dieser Wert wird waehrend eines Reglerzugs gebraucht.
        '''
        ''' WARUM MULTIPLIZIERT WIRD: eine Mischung aus nur Schieben und Xor ist LINEAR, und bei
        ''' einer linearen Mischung heben sich zwei gleiche Bloecke an passenden Stellen gegenseitig
        ''' auf - zwei verschiedene Masken bekaemen denselben Wert und der Zwischenspeicher gaebe
        ''' das falsche Bild zurueck. Die erste Fassung hier war genau so gebaut, und die Diagnose
        ''' hat sie mit zwei Vierecken auffliegen lassen. Multipliziert wird in 32 Bit hinein nach
        ''' 64: das Produkt zweier 32-Bit-Zahlen passt IMMER in 64 Bit und kann deshalb nicht
        ''' ueberlaufen - VB prueft den Ueberlauf und wuerfe sonst mitten im Mischen.</summary>
        Public ReadOnly Property Fingerprint As String
            Get
                If _fingerprint IsNot Nothing Then Return _fingerprint
                Const PrimeA As ULong = 2654435761UL
                Const PrimeB As ULong = 2246822519UL
                Dim h1 As UInteger = &H9E3779B1UI Xor CUInt(Width)
                Dim h2 As UInteger = &H85EBCA77UI Xor CUInt(Height)
                Dim count = Width * Height
                Dim blocks = count \ 4
                For i = 0 To blocks - 1
                    Dim chunk = BitConverter.ToUInt32(_pixels, i * 4)
                    Dim m1 = CULng(h1 Xor chunk) * PrimeA
                    h1 = CUInt(m1 And &HFFFFFFFFUL) Xor CUInt(m1 >> 32)
                    Dim m2 = CULng(h2 Xor h1) * PrimeB
                    h2 = CUInt(m2 And &HFFFFFFFFUL) Xor CUInt(m2 >> 32)
                Next
                For i = blocks * 4 To count - 1
                    Dim m1 = CULng(h1 Xor CUInt(_pixels(i))) * PrimeA
                    h1 = CUInt(m1 And &HFFFFFFFFUL) Xor CUInt(m1 >> 32)
                Next
                Dim mEnd = CULng(h1 Xor h2) * PrimeA
                h1 = CUInt(mEnd And &HFFFFFFFFUL) Xor CUInt(mEnd >> 32)
                _fingerprint = h1.ToString("x8") & h2.ToString("x8")
                Return _fingerprint
            End Get
        End Property
    End Class

    ''' <summary>Ein Paar aus Arbeitsform und Speicherform fuer DIESELBEN Bildpunkte: das Raster
    ''' zum Rechnen, das PNG fuer die Datei. Umgewandelt wird erst, wenn eine Seite gebraucht wird,
    ''' und wer eine Seite setzt, macht die andere ungueltig.
    '''
    ''' WARUM ALS EIGENE KLASSE: Maske, Maskenbestandteil und Auswahl fuehren zusammen sieben
    ''' solcher Paare. Als Feldergruppen ausgeschrieben stand dieselbe Regel siebenmal da - und lief
    ''' prompt auseinander: fuer den Renderschluessel wurde an einer Stelle die Eigenschaft gelesen
    ''' (die wandelt um) und an der anderen der rohe Wert (der nicht). Hier gibt es die Regel EINMAL,
    ''' und <see cref="StoredPng"/>/<see cref="StoredRaster"/> sind der eine Weg daran vorbei.</summary>
    Friend NotInheritable Class AlphaPixels
        Private _png As String = ""
        Private _raster As AlphaRaster
        ' EIN FEHLSCHLAG WIRD GEMERKT. FromPngBase64 gibt Nothing zurueck, wenn die Zeichenkette
        ' kein Alpha-Raster hergibt - ohne diesen Merker versuchte JEDER weitere Zugriff es erneut,
        ' und einer davon ist der Renderschluessel. Ein unlesbares PNG kostete damit bei jedem Bild
        ' einen vollen Dekodierversuch.
        Private _unpackFailed As Boolean

        ''' <summary>Die Speicherform. Sie entsteht erst, wenn jemand sie liest.</summary>
        Public Property Png As String
            Get
                If String.IsNullOrEmpty(_png) AndAlso _raster IsNot Nothing Then _png = _raster.ToPngBase64()
                Return _png
            End Get
            Set(value As String)
                _png = If(value, "")
                _raster = Nothing
                _unpackFailed = False
            End Set
        End Property

        ''' <summary>Die Arbeitsform. Nothing heisst: es gibt keine, und es laesst sich auch keine
        ''' aus der Speicherform gewinnen.</summary>
        Public Property Raster As AlphaRaster
            Get
                If _raster Is Nothing AndAlso Not _unpackFailed AndAlso Not String.IsNullOrEmpty(_png) Then
                    _raster = AlphaRaster.FromPngBase64(_png)
                    _unpackFailed = _raster Is Nothing
                End If
                Return _raster
            End Get
            Set(value As AlphaRaster)
                _raster = value
                _png = ""
                _unpackFailed = False
            End Set
        End Property

        ''' <summary>Was VORLIEGT, ohne eine Umwandlung auszuloesen. Fuer die Wege, die je Render
        ''' oder je Mausbewegung laufen - vor allem den Renderschluessel.</summary>
        Public ReadOnly Property StoredPng As String
            Get
                Return _png
            End Get
        End Property

        Public ReadOnly Property StoredRaster As AlphaRaster
            Get
                Return _raster
            End Get
        End Property

        ''' <summary>Liegen ueberhaupt Bildpunkte vor? Antwortet OHNE zu packen oder zu
        ''' entpacken - die Frage stellen mehrere Wege sehr oft.</summary>
        Public ReadOnly Property HasData As Boolean
            Get
                Return _raster IsNot Nothing OrElse Not String.IsNullOrWhiteSpace(_png)
            End Get
        End Property

        ''' <summary>Uebernimmt ein anderes Paar SO WIE ES VORLIEGT - ein PNG bleibt PNG, ein Raster
        ''' bleibt Raster. Eine Abschrift ueber die Eigenschaften wuerde genau das Packen ausloesen,
        ''' das dieser Weg vermeidet.</summary>
        Public Sub CopyFrom(other As AlphaPixels)
            If other Is Nothing Then Return
            _png = other._png
            _raster = other._raster
            _unpackFailed = other._unpackFailed
        End Sub
    End Class

End Namespace
