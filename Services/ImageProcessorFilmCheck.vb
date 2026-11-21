Imports SkiaSharp

Namespace Services

    ' Die Diaerkennung: sieht ein Scan, auf dem die Filmnegativ-Umkehr eingeschaltet ist, eher nach
    ' einem Positiv aus (Dia, gewoehnliches Foto)? Sie aendert nichts am Bild, Editor und Stapel
    ' zeigen damit nur einen Hinweis.
    Partial Public Class ImageProcessor

        ''' <summary>Was die Diaerkennung an einem Scan misst und wie sie urteilt
        ''' (<see cref="CheckFilmNegative"/>).</summary>
        Public Structure FilmNegativeCheck
            ''' <summary>Sieht der Scan nach einem Positiv aus? Nur dann ein Hinweis.</summary>
            Public LooksPositive As Boolean
            ''' <summary>log10(Rot / Gruen) der Filmbasis im Linearlicht.</summary>
            Public BaseRedOverGreen As Double
            ''' <summary>log10(Rot / Blau) der Filmbasis im Linearlicht.</summary>
            Public BaseRedOverBlue As Double
            ''' <summary>log10(Basis / Dichtepunkt) im Linearlicht, Mittel der drei Kanaele.</summary>
            Public DensityRange As Double
            ''' <summary>Wie verschieden die drei Kanaele ueber der Basis dicht sind, in Dichte
            ''' (log10), 90-Perzentil ueber die Bloecke.</summary>
            Public ChannelSpread As Double
        End Structure

        ''' <summary>Ab hier ist die Basis orange wie bei einem Farbnegativ (C-41): Rot laesst
        ''' mindestens so viel mehr durch als Gruen ...</summary>
        Private Const OrangeBaseRedOverGreen As Double = 0.05
        ''' <summary>... und mindestens so viel mehr als Blau, beides als log10 im Linearlicht.</summary>
        Private Const OrangeBaseRedOverBlue As Double = 0.2
        ''' <summary>Unter dieser Spanne der Kanaldichten ist der Inhalt grau: ein
        ''' Schwarzweissnegativ (oder ein graues Positiv, dort im Zweifel kein Hinweis).</summary>
        Private Const MonochromeChannelSpread As Double = 0.08
        ''' <summary>Ab diesem Dichteumfang reicht der Scan bis ins Schwarz wie ein Positiv; ein
        ''' Negativ bleibt flacher.</summary>
        Private Const PositiveDensityRange As Double = 1.9

        ''' <summary>Prueft einen Scan auf eine Umkehr, die nicht passt. Gemessen wird wie bei der
        ''' Umkehr selbst am BESCHNITTENEN Bild und ohne einen Halter am Rand. Traegt
        ''' <paramref name="adj"/> schon Basis und Dichtepunkt (gemessen oder mit der Pipette
        ''' gesetzt), gelten die; sonst werden sie hier gemessen.
        '''
        ''' Drei Fragen, in dieser Reihenfolge, und jede kann nur ENTWARNEN:
        ''' 1. Ist die Basis orange? Dann ist es ein Farbnegativ.
        ''' 2. Ist der Inhalt grau (alle drei Kanaele ueberall gleich dicht ueber der Basis, auch
        '''    bei getoenter Basis)? Dann ist es ein Schwarzweissnegativ; ein graues Positiv bekommt
        '''    im Zweifel ebenfalls keinen Hinweis.
        ''' 3. Ist der Dichteumfang flach wie bei einem Negativ? Dann auch keiner.
        ''' Erst was alle drei Fragen verneint, sieht nach einem Positiv aus. Die Schwellen sind an
        ''' den Testnegativen der Ablage, an Diascans und an Positiven gemessen
        ''' (packaging/Diagnostics/Diaerkennung).</summary>
        Public Shared Function CheckFilmNegative(source As SKBitmap, adj As ImageAdjustments) As FilmNegativeCheck
            Dim result = New FilmNegativeCheck()
            If source Is Nothing Then Return result
            ' Die Quelle gehoert dem Aufrufer, wie bei AnalyzeFilmNegative.
            Dim owned = False
            Dim cropped = ApplyGeometryPipelineOwned(source, If(adj, New ImageAdjustments()), owned)
            Try
                Dim hasBase = adj IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(adj.NegativeBaseColor)
                Dim hasDensity = adj IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(adj.NegativeDensityColor)
                Dim baseColor As SKColor
                Dim densityColor As SKColor
                If hasBase AndAlso hasDensity Then
                    baseColor = ParseColor(adj.NegativeBaseColor, SKColors.White)
                    densityColor = ParseColor(adj.NegativeDensityColor, SKColors.Black)
                Else
                    Dim measured = AnalyzeFilmNegativeCore(cropped)
                    baseColor = If(hasBase, ParseColor(adj.NegativeBaseColor, measured.BaseColor), measured.BaseColor)
                    densityColor = If(hasDensity, ParseColor(adj.NegativeDensityColor, measured.DensityColor), measured.DensityColor)
                End If

                Dim base = {FilmCheckLinear(baseColor.Red), FilmCheckLinear(baseColor.Green), FilmCheckLinear(baseColor.Blue)}
                Dim density = {FilmCheckLinear(densityColor.Red), FilmCheckLinear(densityColor.Green), FilmCheckLinear(densityColor.Blue)}
                result.BaseRedOverGreen = Math.Log10(base(0) / base(1))
                result.BaseRedOverBlue = Math.Log10(base(0) / base(2))
                result.DensityRange = (Math.Log10(base(0) / density(0)) + Math.Log10(base(1) / density(1)) +
                                       Math.Log10(base(2) / density(2))) / 3.0
                result.ChannelSpread = MeasureFilmChannelSpread(cropped, base, result.DensityRange)

                Dim orange = result.BaseRedOverGreen >= OrangeBaseRedOverGreen AndAlso
                             result.BaseRedOverBlue >= OrangeBaseRedOverBlue
                Dim monochrome = result.ChannelSpread < MonochromeChannelSpread
                result.LooksPositive = Not orange AndAlso Not monochrome AndAlso
                                       result.DensityRange >= PositiveDensityRange
                Return result
            Finally
                If owned Then cropped?.Dispose()
            End Try
        End Function

        ''' <summary>Dasselbe fuer eine Datei, fuer den Stapel: Basis und Dichtepunkt werden an
        ''' DIESEM Bild gemessen, nie aus <paramref name="adj"/> genommen (eingefuegte Anpassungen
        ''' tragen die Messung des Bildes, von dem sie kopiert wurden). Eine RAW wird halb gross
        ''' entwickelt, wie fuer die Kacheln, und im Modell 1 linear wie fuer die Umkehr. Ein .fpx
        ''' kommt nur samt Umkehr gerendert heraus und wird nicht geprueft.</summary>
        Public Shared Function CheckFilmNegativeFile(path As String, adj As ImageAdjustments) As FilmNegativeCheck
            If String.IsNullOrWhiteSpace(path) OrElse Not IO.File.Exists(path) OrElse FpxService.IsFpx(path) Then
                Return New FilmNegativeCheck()
            End If
            Dim measureAdj = If(adj, New ImageAdjustments()).Clone()
            measureAdj.NegativeBaseColor = ""
            measureAdj.NegativeDensityColor = ""
            Dim bmp As SKBitmap = Nothing
            Try
                If RawPreviewService.IsSupportedRaw(path) Then
                    bmp = RawDecodeService.TryDecodeThumbnail(path, LensChoiceFrom(measureAdj), measureAdj.RawHighlightRecovery,
                                                              0.0, measureAdj.RawHighlightKnee())
                End If
                If bmp Is Nothing Then bmp = DecodeForAdjustments(path, measureAdj)
                If bmp Is Nothing Then Return New FilmNegativeCheck()
                Return CheckFilmNegative(bmp, measureAdj)
            Catch ex As Exception
                DiagnosticLogService.LogException("ImageProcessor.CheckFilmNegativeFile", ex)
                Return New FilmNegativeCheck()
            Finally
                bmp?.Dispose()
            End Try
        End Function

        ''' <summary>sRGB-Stufe nach Linearlicht, nach unten begrenzt: ein Kanal auf 0 gaebe
        ''' einen unendlichen Dichteumfang.</summary>
        Private Shared Function FilmCheckLinear(value As Byte) As Double
            Return Math.Max(1.0 / 4096.0, SrgbToLinear(value / 255.0))
        End Function

        ''' <summary>Wie verschieden die Kanaele ueber der Basis dicht sind: je Block (dieselben
        ''' Bloecke wie bei der Messung der Umkehr, ohne Halter) die Dichte jedes Kanals relativ zur
        ''' Basis, davon die Spanne zwischen dem dichtesten und dem duennsten Kanal; zurueck kommt
        ''' das 90-Perzentil. Gezaehlt werden nur Bloecke zwischen 5 und 85 Prozent des Umfangs:
        ''' an der Basis ist jede Spanne 0, im dichtesten Teil traegt das Rauschen.
        '''
        ''' Relativ zur Basis, weil ein Schwarzweissfilm einen getoenten Traeger haben kann; sein
        ''' Bild ist trotzdem in allen Kanaelen gleich dicht.</summary>
        Private Shared Function MeasureFilmChannelSpread(bmp As SKBitmap, base As Double(), densityRange As Double) As Double
            Dim buffer As Byte() = Nothing
            Dim stride As Integer = 0
            If bmp Is Nothing OrElse Not TryBorrowBgraBuffer(bmp, buffer, stride) Then Return 0.0
            Dim stepPx = Math.Max(1, CInt(Math.Sqrt(bmp.Width * CDbl(bmp.Height) / 250000.0)))
            Dim area = FindFilmHolderArea(buffer, stride, bmp.Width, bmp.Height, stepPx)
            Dim toLinear = New Double(255) {}
            For i = 0 To 255
                toLinear(i) = SrgbToLinear(i / 255.0)
            Next
            Dim lowest = 0.05 * densityRange
            Dim highest = 0.85 * densityRange
            ' Spanne in Hundertstel Dichte, 0 bis 3.
            Dim hist = New Integer(300) {}
            Dim total = 0
            For y As Integer = area.Top To area.Bottom - 1 Step stepPx
                Dim yEnd = Math.Min(area.Bottom, y + stepPx)
                For x As Integer = area.Left To area.Right - 1 Step stepPx
                    Dim xEnd = Math.Min(area.Right, x + stepPx)
                    Dim sumB = 0.0, sumG = 0.0, sumR = 0.0
                    Dim count = 0
                    For by As Integer = y To yEnd - 1
                        Dim row = by * stride
                        For bx As Integer = x To xEnd - 1
                            Dim o = row + bx * 4
                            If buffer(o + 3) < 8 Then Continue For
                            sumB += toLinear(buffer(o))
                            sumG += toLinear(buffer(o + 1))
                            sumR += toLinear(buffer(o + 2))
                            count += 1
                        Next
                    Next
                    If count = 0 Then Continue For
                    Dim dR = -Math.Log10(Math.Max(1.0E-4, sumR / count / base(0)))
                    Dim dG = -Math.Log10(Math.Max(1.0E-4, sumG / count / base(1)))
                    Dim dB = -Math.Log10(Math.Max(1.0E-4, sumB / count / base(2)))
                    Dim mean = (dR + dG + dB) / 3.0
                    If mean < lowest OrElse mean > highest Then Continue For
                    Dim spread = Math.Max(dR, Math.Max(dG, dB)) - Math.Min(dR, Math.Min(dG, dB))
                    hist(Math.Min(300, CInt(Math.Round(spread * 100.0)))) += 1
                    total += 1
                Next
            Next
            If total = 0 Then Return 0.0
            Dim target = CLng(Math.Ceiling(total * 0.9))
            Dim running = 0L
            For i = 0 To 300
                running += hist(i)
                If running >= target Then Return i / 100.0
            Next
            Return 3.0
        End Function

    End Class

End Namespace
