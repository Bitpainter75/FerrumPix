Imports System
Imports System.Globalization
Imports System.Linq
Imports FerrumPix.Services

Namespace Services

    ''' <summary>
    ''' Baut den Metadaten-Stand eines Bildes fuer das Infopanel.
    '''
    ''' Warum ein eigener Dienst: dasselbe Panel haengt inzwischen an Betrachter, Editor und Galerie.
    ''' Solange die beiden Funktionen im Betrachter steckten, haette jede weitere Ansicht sie kopieren
    ''' muessen - und Kopien laufen auseinander. Zustand brauchen sie keinen.
    ''' </summary>
    Public NotInheritable Class ImageInfoService

        Private Sub New()
        End Sub

        Public Shared Function BuildImageInfo(imagePath As String, imageWidth As Integer, imageHeight As Integer) As ExifData
            Dim data = ExifService.ReadExif(imagePath)

            Dim effectiveWidth = imageWidth
            Dim effectiveHeight = imageHeight
            If effectiveWidth <= 0 OrElse effectiveHeight <= 0 Then
                ' Beim asynchronen FPX-Wechsel ist die Composite-Vorschau unter Umstaenden noch nicht
                ' publiziert. Dann die soeben aus composite.png gelesenen Masse verwenden, statt MP
                ' und Seitenverhaeltnis im Infopanel leer zu lassen.
                Dim sourceDimensions = ExifService.ExtractSearchFields(data)
                effectiveWidth = sourceDimensions.ImageWidth.GetValueOrDefault()
                effectiveHeight = sourceDimensions.ImageHeight.GetValueOrDefault()
            End If

            If effectiveWidth > 0 AndAlso effectiveHeight > 0 Then
                ' DIE ECHTEN MASSE GEWINNEN, nicht die Angabe aus den Metadaten. Die trug bei einer
                ' NEF den ganzen Sensorrahmen samt abgedecktem Rand (4992 x 3280 statt 4928 x 3264,
                ' Nutzerbefund), und nach einem Zuschnitt ohne EXIF-Pflege die Masse von vorher.
                ' Megapixel und Seitenverhaeltnis darunter rechnen ohnehin mit diesen Werten; die
                ' Zeile darueber sagte also etwas anderes als die zwei unter ihr.
                data.ImageWidth = effectiveWidth.ToString(Globalization.CultureInfo.InvariantCulture)
                data.ImageHeight = effectiveHeight.ToString(Globalization.CultureInfo.InvariantCulture)

                Dim mp = effectiveWidth * effectiveHeight / 1_000_000.0
                data.Megapixels = $"{mp:F1} MP"
                data.AspectRatio = FormatAspectRatio(effectiveWidth, effectiveHeight)
            End If

            If String.IsNullOrWhiteSpace(data.FileType) Then
                data.FileType = IO.Path.GetExtension(imagePath).TrimStart("."c).ToUpperInvariant()
            End If

            If String.IsNullOrWhiteSpace(data.ColorSpace) Then data.ColorSpace = "Unbekannt"

            Return data
        End Function

        ''' <summary>Provisorischer Infopanel-Stand aus dem Katalog (SQLite, ein Zeilen-Lookup) -
        ''' zeigt beim Bildwechsel sofort die Daten des RICHTIGEN Bildes, bis der vollständige
        ''' EXIF-Read sie ersetzt. Kennt der Katalog das Bild nicht, kommt ein MINIMAL-Objekt
        ''' (Dateiname/Typ, Rest leer) zurück - NIE Nothing: Bindings wie „ExifInfo.Camera"
        ''' aktualisieren bei Nothing nicht auf leer, sondern behalten stumpf den letzten Wert -
        ''' genau so blieb das Panel beim Filmstrip-Wechsel auf dem Vorgängerbild stehen
        ''' (3. Runde).</summary>
        Public Shared Function BuildProvisionalFromCatalog(imagePath As String) As ExifData
            Try
                Dim meta = LibraryService.Instance.GetMetaForPaths({imagePath}).Values.FirstOrDefault()
                If meta Is Nothing Then
                    Dim minimal As New ExifData With {
                        .FileName = IO.Path.GetFileName(imagePath),
                        .FolderPath = If(IO.Path.GetDirectoryName(imagePath), ""),
                        .FileType = IO.Path.GetExtension(imagePath).TrimStart("."c).ToUpperInvariant()
                    }
                    ExifService.FillFileFacts(minimal, imagePath)
                    Return minimal
                End If

                ' Der Ordner kommt aus dem PFAD und nicht aus dem Katalog: dort steht er nicht,
                ' und er ist ohnehin bekannt - dieser Weg baut den ersten Stand der Info-Leiste,
                ' noch bevor die Datei gelesen wird. Ohne ihn blieb die Zeile leer, bis der
                ' Hintergrundlauf fertig war, und beim Blaettern war das jedes Mal aufs Neue.
                Dim data As New ExifData With {
                    .FileName = IO.Path.GetFileName(imagePath),
                    .FolderPath = If(IO.Path.GetDirectoryName(imagePath), ""),
                    .FileType = IO.Path.GetExtension(imagePath).TrimStart("."c).ToUpperInvariant(),
                    .DateTaken = If(meta.DateTaken, ""),
                    .DateModifiedExif = If(meta.DateModifiedExif, ""),
                    .Camera = If(meta.Camera, ""),
                    .Lens = If(meta.Lens, ""),
                    .ShutterSpeed = If(meta.ShutterSpeed, "")
                }
                If meta.Aperture.HasValue Then data.Aperture = "f/" & meta.Aperture.Value.ToString("0.#", Globalization.CultureInfo.InvariantCulture)
                If meta.FocalLengthMm.HasValue Then data.FocalLength = meta.FocalLengthMm.Value.ToString("0.#", Globalization.CultureInfo.InvariantCulture) & " mm"
                If meta.Iso.HasValue Then data.ISO = meta.Iso.Value.ToString(Globalization.CultureInfo.InvariantCulture)
                ExifService.FillFileFacts(data, imagePath)
                If meta.ImageWidth.GetValueOrDefault() > 0 AndAlso meta.ImageHeight.GetValueOrDefault() > 0 Then
                    Dim w = meta.ImageWidth.Value
                    Dim h = meta.ImageHeight.Value
                    data.ImageWidth = w.ToString(Globalization.CultureInfo.InvariantCulture)
                    data.ImageHeight = h.ToString(Globalization.CultureInfo.InvariantCulture)
                    data.Megapixels = $"{w * h / 1_000_000.0:F1} MP"
                    data.AspectRatio = FormatAspectRatio(w, h)
                End If
                Return data
            Catch
                ' Auch im Fehlerfall nie Nothing (siehe Methodenkommentar - Bindings blieben sonst
                ' auf dem Vorgängerbild stehen).
                Return New ExifData With {
                    .FileName = IO.Path.GetFileName(If(imagePath, ""))
                }
            End Try
        End Function

        ''' <summary>Der Kameraname fuer die Info-Leiste, ohne doppelte Marke.
        '''
        ''' Die Zeile setzt sich aus Hersteller und Modell zusammen, und viele Hersteller
        ''' wiederholen ihren Namen im Modell: "Canon" + "Canon EOS M50", "NIKON CORPORATION" +
        ''' "NIKON D7000". Das ergab "Canon Canon EOS M50" und "NIKON CORPORATION NIKON D7000", und
        ''' in einer schmalen Leiste drei Zeilen fuer eine Kamera. Kommt das erste Wort im Rest
        ''' noch einmal vor, beginnt der Name dort; sonst fallen Firmenzusaetze direkt nach dem
        ''' ersten Wort weg ("OLYMPUS IMAGING CORP. E-M10" wird "OLYMPUS E-M10").
        '''
        ''' NUR FUER DIE ANZEIGE. Katalog, Suche und Filter behalten die Schreibweise der Datei;
        ''' eine geaenderte stuende dort neben der alten, bis jede Datei neu gelesen ist.</summary>
        Public Shared Function DisplayCameraName(camera As String) As String
            Dim words = If(camera, "").Split(" "c, StringSplitOptions.RemoveEmptyEntries)
            If words.Length < 2 Then Return If(camera, "").Trim()
            For i = 1 To words.Length - 1
                If String.Equals(words(i), words(0), StringComparison.OrdinalIgnoreCase) Then
                    Return String.Join(" ", words.Skip(i))
                End If
            Next
            Dim suffixes = {"CORPORATION", "CORP.", "CORP", "IMAGING", "COMPANY", "COMPANY,", "CO.,", "CO.", "LTD.", "LTD", "INC.", "INC"}
            Dim rest = words.Skip(1).SkipWhile(Function(w) suffixes.Contains(w.ToUpperInvariant())).ToArray()
            ' Bleibt nach den Zusaetzen nichts uebrig, war der Zusatz das Modell - dann alles zeigen.
            If rest.Length = 0 Then Return String.Join(" ", words)
            Return words(0) & " " & String.Join(" ", rest)
        End Function

        ''' <summary>Das Seitenverhaeltnis gekuerzt, etwa 3:2. Stand vorher zweimal im Quelltext,
        ''' einmal im Betrachter und einmal im Editor.</summary>
        Public Shared Function FormatAspectRatio(width As Integer, height As Integer) As String
            If width <= 0 OrElse height <= 0 Then Return ""

            Dim divisor = GreatestCommonDivisor(width, height)
            Return $"{width \ divisor}:{height \ divisor}"
        End Function

        Private Shared Function GreatestCommonDivisor(a As Integer, b As Integer) As Integer
            a = Math.Abs(a)
            b = Math.Abs(b)

            While b <> 0
                Dim remainder = a Mod b
                a = b
                b = remainder
            End While

            Return Math.Max(1, a)
        End Function

    End Class

End Namespace
