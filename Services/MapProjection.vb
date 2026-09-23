Namespace Services

    ''' <summary>Web-Mercator, wie ihn jeder Kachelserver im Netz verwendet.
    '''
    ''' Gerechnet wird in WELTPIXELN einer ganzzahligen Zoomstufe: auf Stufe z ist die Welt
    ''' 256 mal 2^z Punkte breit und ebenso hoch, links oben liegt 180 Grad West auf der
    ''' Breite, an der die Projektion abschneidet. Eine Kachel (x, y) deckt dann genau die
    ''' Weltpixel x*256 bis x*256+255 ab - Kachelnummern und Bildschirmrechnung fallen damit
    ''' in dieselbe Einheit.
    '''
    ''' Die Pole gibt es in dieser Projektion nicht: sie lägen unendlich weit weg. Bei rund
    ''' 85,05 Grad ist die Karte quadratisch, und dort schneiden alle Anbieter ab.</summary>
    Public Module MapProjection

        Public Const TileSize As Integer = 256

        ''' <summary>Die Breite, an der die quadratische Weltkarte endet.</summary>
        Public Const MaxLatitude As Double = 85.051128779806589

        Public Function WorldSize(zoom As Integer) As Double
            Return TileSize * Math.Pow(2, zoom)
        End Function

        Public Function TileCount(zoom As Integer) As Integer
            Return 1 << Math.Max(0, Math.Min(30, zoom))
        End Function

        Public Function LongitudeToWorldX(longitude As Double, zoom As Integer) As Double
            Return (longitude + 180.0) / 360.0 * WorldSize(zoom)
        End Function

        Public Function LatitudeToWorldY(latitude As Double, zoom As Integer) As Double
            Dim clamped = Math.Max(-MaxLatitude, Math.Min(MaxLatitude, latitude))
            Dim radians = clamped * Math.PI / 180.0
            Dim mercator = Math.Log(Math.Tan(radians) + 1.0 / Math.Cos(radians))
            Return (1.0 - mercator / Math.PI) / 2.0 * WorldSize(zoom)
        End Function

        Public Function WorldXToLongitude(worldX As Double, zoom As Integer) As Double
            Return worldX / WorldSize(zoom) * 360.0 - 180.0
        End Function

        Public Function WorldYToLatitude(worldY As Double, zoom As Integer) As Double
            Dim n = Math.PI * (1.0 - 2.0 * worldY / WorldSize(zoom))
            Return Math.Atan(Math.Sinh(n)) * 180.0 / Math.PI
        End Function

        ''' <summary>Kachelspalte auf die Welt zurückfalten. Östlich von 180 Grad beginnt die Welt
        ''' von vorn - wer über die Datumsgrenze schwenkt, sieht wieder Alaska und keinen
        ''' leeren Rand.</summary>
        Public Function WrapTileX(x As Long, zoom As Integer) As Integer
            Dim n = CLng(TileCount(zoom))
            Dim wrapped = x Mod n
            If wrapped < 0 Then wrapped += n
            Return CInt(wrapped)
        End Function

    End Module

End Namespace
