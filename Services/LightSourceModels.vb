Option Strict On
Option Explicit On

Imports System.Collections.Generic
Imports System.Globalization

' Die Lichtquellen des Werkzeugs "Beleuchtung": frei auf dem Bild platzierte Lichter (und mit
' negativer Leuchtkraft Schatten), die beim Rendern gerechnet werden. Sie stehen im Rezept und damit
' in der .fpxmp und im .fpx-Buendel; eingebacken wird nichts. Gerechnet werden sie in
' ImageProcessorLights.vb.
Namespace Services

    ''' <summary>Eine Lichtquelle. Lage und Groessen in Prozent des gerenderten Bildes, wie die Mitte
    ''' der Vignette: Reichweite in Prozent der kuerzeren Seite.</summary>
    Public Class LightSource
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        Public Property Enabled As Boolean = True

        ''' <summary>Die Form: <see cref="LightSourceKinds"/>.</summary>
        Public Property Kind As String = LightSourceKinds.Point
        ''' <summary>Die Lichtart, aus der die Werte stammen (nur fuer die Anzeige, siehe
        ''' <see cref="LightPresets"/>). Leer, sobald jemand einen Wert davon wegbewegt hat, bleibt
        ''' aber stehen: sie sagt nur, womit es anfing.</summary>
        Public Property Preset As String = ""

        Public Property XPercent As Double = 50
        Public Property YPercent As Double = 50
        ''' <summary>Reichweite in Prozent der kuerzeren Bildseite.</summary>
        Public Property RadiusPercent As Double = 30
        ''' <summary>Leuchtkraft in Blendenstufen. Negativ ist ein Schatten.</summary>
        Public Property Intensity As Double = 1.0
        ''' <summary>Richtung in Grad, 0 = nach rechts, 90 = nach unten (Spot, Flaeche, Streiflicht).</summary>
        Public Property AngleDegrees As Double = 90
        ''' <summary>Oeffnungswinkel des Kegels in Grad (nur Spot).</summary>
        Public Property ConeDegrees As Double = 50
        ''' <summary>Seitenverhaeltnis der Flaeche, Breite durch Hoehe in Prozent (nur Flaeche).</summary>
        Public Property AspectPercent As Double = 100
        ''' <summary>Weiche des Randes, 0 bis 100.</summary>
        Public Property Softness As Double = 50
        ''' <summary>Abfall: <see cref="LightFalloffs"/>.</summary>
        Public Property Falloff As String = LightFalloffs.Natural

        ''' <summary>True: Farbe aus der Farbtemperatur, sonst aus <see cref="Color"/>.</summary>
        Public Property UseTemperature As Boolean = True
        Public Property TemperatureKelvin As Double = 3200
        Public Property Color As String = "#FFFFFFFF"

        ''' <summary><see cref="LightBlendModes"/>: Beleuchten wirkt wie Licht bei der Aufnahme,
        ''' Leuchten legt einen sichtbaren Schein auch auf Schwarz.</summary>
        Public Property BlendMode As String = LightBlendModes.Illuminate
        ''' <summary>Lichthof um die Quelle selbst, 0 bis 100.</summary>
        Public Property Glow As Double = 0
        ''' <summary>Strahlen (nur Art Strahlen): Anzahl und wie deutlich sie sich abheben.</summary>
        Public Property RayCount As Double = 14
        Public Property RayContrast As Double = 60

        ''' <summary>Tiefenbewusst: das Licht trifft vor allem, was in der Tiefe der Quelle liegt.
        ''' Braucht das Tiefenmodell; ohne Karte wirkt das Licht wie ohne den Schalter.</summary>
        Public Property UseDepth As Boolean = False
        ''' <summary>Wie weit das Licht in der Tiefe reicht, 0 bis 100.</summary>
        Public Property DepthReach As Double = 25

        Public Function Clone() As LightSource
            Return DirectCast(MemberwiseClone(), LightSource)
        End Function

        ''' <summary>Alles, was das Bild beeinflusst, als Text fuer den Zwischenspeicher-Schluessel.</summary>
        Public Function Signature() As String
            Return String.Join(",", New Object() {
                Enabled, Kind, XPercent, YPercent, RadiusPercent, Intensity, AngleDegrees, ConeDegrees,
                AspectPercent, Softness, Falloff, UseTemperature, TemperatureKelvin, Color, BlendMode, Glow,
                RayCount, RayContrast, UseDepth, DepthReach, Id
            }.Select(Function(v) Convert.ToString(v, CultureInfo.InvariantCulture)))
        End Function
    End Class

    Public NotInheritable Class LightSourceKinds
        Public Const Point As String = "Point"
        Public Const Spot As String = "Spot"
        Public Const Area As String = "Area"
        Public Const Streak As String = "Streak"
        Public Const Rays As String = "Rays"

        Public Shared ReadOnly All As String() = {Point, Spot, Area, Streak, Rays}

        ''' <summary>Der deutsche Ausgangstext der Art; uebersetzt wird beim Anzeigen.</summary>
        Public Shared Function Label(kind As String) As String
            Select Case kind
                Case Spot : Return "Spot"
                Case Area : Return "Fläche"
                Case Streak : Return "Streiflicht"
                Case Rays : Return "Strahlen"
                Case Else : Return "Punktlicht"
            End Select
        End Function

        Public Shared Function Normalize(kind As String) As String
            For Each k In All
                If String.Equals(k, kind, StringComparison.OrdinalIgnoreCase) Then Return k
            Next
            Return Point
        End Function
    End Class

    Public NotInheritable Class LightFalloffs
        Public Const Soft As String = "Soft"
        Public Const Natural As String = "Natural"
        Public Const Hard As String = "Hard"
        Public Shared ReadOnly All As String() = {Soft, Natural, Hard}

        Public Shared Function Label(falloff As String) As String
            Select Case falloff
                Case Soft : Return "Weich"
                Case Hard : Return "Hart"
                Case Else : Return "Natürlich"
            End Select
        End Function
    End Class

    Public NotInheritable Class LightBlendModes
        Public Const Illuminate As String = "Illuminate"
        Public Const Glow As String = "Glow"
        Public Shared ReadOnly All As String() = {Illuminate, Glow}

        Public Shared Function Label(mode As String) As String
            Return If(mode = Glow, "Leuchten", "Beleuchten")
        End Function
    End Class

    ''' <summary>Eine Lichtart: Vorgaben fuer eine neue Lichtquelle. Danach ist jeder Wert frei.</summary>
    Public NotInheritable Class LightPreset
        Public Property Key As String
        ''' <summary>Deutscher Ausgangstext, uebersetzt beim Anzeigen.</summary>
        Public Property Label As String
        Public Property Kind As String
        Public Property Kelvin As Double
        ''' <summary>Leer: Farbtemperatur. Sonst eine feste Farbe.</summary>
        Public Property Color As String = ""
        Public Property Intensity As Double
        Public Property RadiusPercent As Double
        Public Property ConeDegrees As Double = 50
        Public Property AngleDegrees As Double = 90
        Public Property Softness As Double
        Public Property Falloff As String = LightFalloffs.Natural
        Public Property BlendMode As String = LightBlendModes.Illuminate
        Public Property Glow As Double
        Public Property AspectPercent As Double = 100

        Public Sub ApplyTo(light As LightSource)
            light.Preset = Key
            light.Kind = Kind
            light.UseTemperature = String.IsNullOrEmpty(Color)
            light.TemperatureKelvin = If(Kelvin > 0, Kelvin, light.TemperatureKelvin)
            If Not String.IsNullOrEmpty(Color) Then light.Color = Color
            light.Intensity = Intensity
            light.RadiusPercent = RadiusPercent
            light.ConeDegrees = ConeDegrees
            light.AngleDegrees = AngleDegrees
            light.Softness = Softness
            light.Falloff = Falloff
            light.BlendMode = BlendMode
            light.Glow = Glow
            light.AspectPercent = AspectPercent
        End Sub
    End Class

    ''' <summary>Die Lichtarten der Auswahlliste. Die Farbtemperaturen sind die ueblichen Werte der
    ''' Leuchtmittel; Leuchtkraft, Reichweite und Lichthof sind so gewaehlt, dass die Art auf einem
    ''' normal belichteten Foto wiederzuerkennen ist.</summary>
    Public NotInheritable Class LightPresets
        Public Shared ReadOnly All As LightPreset() = {
            New LightPreset With {.Key = "Candle", .Label = "Kerze", .Kind = LightSourceKinds.Point, .Kelvin = 1850,
                                  .Intensity = 1.6, .RadiusPercent = 18, .Softness = 70, .Falloff = LightFalloffs.Natural, .Glow = 45},
            New LightPreset With {.Key = "Campfire", .Label = "Lagerfeuer", .Kind = LightSourceKinds.Point, .Kelvin = 1900,
                                  .Intensity = 2.0, .RadiusPercent = 40, .Softness = 80, .Falloff = LightFalloffs.Natural, .Glow = 35},
            New LightPreset With {.Key = "Bulb", .Label = "Glühbirne", .Kind = LightSourceKinds.Point, .Kelvin = 2700,
                                  .Intensity = 1.6, .RadiusPercent = 35, .Softness = 60, .Falloff = LightFalloffs.Natural, .Glow = 25},
            New LightPreset With {.Key = "Halogen", .Label = "Halogen", .Kind = LightSourceKinds.Spot, .Kelvin = 3200,
                                  .Intensity = 1.8, .RadiusPercent = 55, .ConeDegrees = 45, .Softness = 40, .Falloff = LightFalloffs.Natural, .Glow = 10},
            New LightPreset With {.Key = "LedWarm", .Label = "LED warmweiß", .Kind = LightSourceKinds.Point, .Kelvin = 3000,
                                  .Intensity = 1.4, .RadiusPercent = 35, .Softness = 45, .Falloff = LightFalloffs.Soft, .Glow = 8},
            New LightPreset With {.Key = "LedCool", .Label = "LED kaltweiß", .Kind = LightSourceKinds.Point, .Kelvin = 5500,
                                  .Intensity = 1.4, .RadiusPercent = 35, .Softness = 40, .Falloff = LightFalloffs.Soft, .Glow = 8},
            New LightPreset With {.Key = "Fluorescent", .Label = "Leuchtstoffröhre", .Kind = LightSourceKinds.Area, .Kelvin = 4300, .Color = "#FFEFFFF2",
                                  .Intensity = 1.2, .RadiusPercent = 45, .Softness = 70, .Falloff = LightFalloffs.Soft, .AspectPercent = 400, .AngleDegrees = 0},
            New LightPreset With {.Key = "Softbox", .Label = "Softbox", .Kind = LightSourceKinds.Area, .Kelvin = 5600,
                                  .Intensity = 1.2, .RadiusPercent = 55, .Softness = 85, .Falloff = LightFalloffs.Soft, .AspectPercent = 130, .AngleDegrees = 0},
            New LightPreset With {.Key = "Streetlight", .Label = "Straßenlaterne", .Kind = LightSourceKinds.Spot, .Kelvin = 2000, .Color = "#FFFFB45A",
                                  .Intensity = 1.8, .RadiusPercent = 70, .ConeDegrees = 80, .AngleDegrees = 90, .Softness = 55, .Falloff = LightFalloffs.Natural, .Glow = 30},
            New LightPreset With {.Key = "Floodlight", .Label = "Flutlicht", .Kind = LightSourceKinds.Spot, .Kelvin = 5700,
                                  .Intensity = 2.4, .RadiusPercent = 120, .ConeDegrees = 70, .AngleDegrees = 60, .Softness = 35, .Falloff = LightFalloffs.Soft, .Glow = 20},
            New LightPreset With {.Key = "Flashlight", .Label = "Taschenlampe", .Kind = LightSourceKinds.Spot, .Kelvin = 6200,
                                  .Intensity = 2.0, .RadiusPercent = 60, .ConeDegrees = 22, .AngleDegrees = 0, .Softness = 20, .Falloff = LightFalloffs.Hard, .Glow = 12},
            New LightPreset With {.Key = "EveningSun", .Label = "Abendsonne", .Kind = LightSourceKinds.Streak, .Kelvin = 3300,
                                  .Intensity = 1.3, .RadiusPercent = 110, .AngleDegrees = 20, .Softness = 80, .Falloff = LightFalloffs.Soft},
            New LightPreset With {.Key = "SunRays", .Label = "Sonnenstrahlen", .Kind = LightSourceKinds.Rays, .Kelvin = 4500,
                                  .Intensity = 1.4, .RadiusPercent = 110, .Softness = 60, .Falloff = LightFalloffs.Soft, .BlendMode = LightBlendModes.Glow, .Glow = 30},
            New LightPreset With {.Key = "Moonlight", .Label = "Mondlicht", .Kind = LightSourceKinds.Area, .Kelvin = 8000,
                                  .Intensity = 0.8, .RadiusPercent = 90, .Softness = 90, .Falloff = LightFalloffs.Soft, .AspectPercent = 100},
            New LightPreset With {.Key = "NeonPink", .Label = "Neon Pink", .Kind = LightSourceKinds.Point, .Color = "#FFFF2E9A",
                                  .Intensity = 1.8, .RadiusPercent = 30, .Softness = 60, .Falloff = LightFalloffs.Natural, .BlendMode = LightBlendModes.Glow, .Glow = 50},
            New LightPreset With {.Key = "NeonBlue", .Label = "Neon Blau", .Kind = LightSourceKinds.Point, .Color = "#FF2E8BFF",
                                  .Intensity = 1.8, .RadiusPercent = 30, .Softness = 60, .Falloff = LightFalloffs.Natural, .BlendMode = LightBlendModes.Glow, .Glow = 50},
            New LightPreset With {.Key = "Colored", .Label = "Farbiges Licht", .Kind = LightSourceKinds.Spot, .Color = "#FF7A3DFF",
                                  .Intensity = 1.6, .RadiusPercent = 60, .ConeDegrees = 55, .Softness = 50, .Falloff = LightFalloffs.Natural},
            New LightPreset With {.Key = "Shadow", .Label = "Schatten", .Kind = LightSourceKinds.Area, .Kelvin = 6500,
                                  .Intensity = -1.5, .RadiusPercent = 35, .Softness = 75, .Falloff = LightFalloffs.Soft, .AspectPercent = 100},
            New LightPreset With {.Key = "DropShadow", .Label = "Schlagschatten", .Kind = LightSourceKinds.Streak, .Kelvin = 6500,
                                  .Intensity = -1.2, .RadiusPercent = 60, .AngleDegrees = 200, .Softness = 60, .Falloff = LightFalloffs.Soft}
        }

        Public Shared Function Find(key As String) As LightPreset
            For Each p In All
                If String.Equals(p.Key, key, StringComparison.Ordinal) Then Return p
            Next
            Return Nothing
        End Function
    End Class

End Namespace
