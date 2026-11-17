Imports System

Namespace Services

    ' One contract for controls, new recipes, loaded recipes and rendering. Narrowed limits
    ' apply to old recipes too, so an out-of-range saved value cannot bypass today's controls.
    Public NotInheritable Class FilmEffectParameters
        Private Sub New()
        End Sub

        Public Const SiliconBonkEnabledDefault As Boolean = False
        Public Const SiliconBonkPreserveDetailsDefault As Boolean = False
        Public Const FatToniEnabledDefault As Boolean = False

        Public Shared ReadOnly SiliconBonkHue As New FilmParameterDefinition(0, 360, 45)
        Public Shared ReadOnly SiliconBonkLuma As New FilmParameterDefinition(-100, 100, 0)
        Public Shared ReadOnly SiliconBonkSaturation As New FilmParameterDefinition(0, 200, 0)
        Public Shared ReadOnly SiliconBonkMidpoint As New FilmParameterDefinition(-100, 100, 0)
        Public Shared ReadOnly SiliconBonkContrast As New FilmParameterDefinition(-50, 200, 0)
        Public Shared ReadOnly FatToniHighlightHue As New FilmParameterDefinition(0, 360, 0)
        Public Shared ReadOnly FatToniHighlightSaturation As New FilmParameterDefinition(0, 100, 0)
        Public Shared ReadOnly FatToniShadowHue As New FilmParameterDefinition(0, 360, 0)
        Public Shared ReadOnly FatToniShadowSaturation As New FilmParameterDefinition(0, 100, 0)
        Public Shared ReadOnly FatToniSeparation As New FilmParameterDefinition(0, 100, 50)
        Public Shared ReadOnly FatToniMidpoint As New FilmParameterDefinition(-100, 100, 0)
        Public Shared ReadOnly FatToniContrast As New FilmParameterDefinition(0, 100, 85)
        Public Shared ReadOnly FatToniBaseHue As New FilmParameterDefinition(0, 360, 0)
        Public Shared ReadOnly FatToniBaseSaturation As New FilmParameterDefinition(0, 100, 0)
        Public Shared ReadOnly FatToniBaseLuminance As New FilmParameterDefinition(0, 100, 100)

    End Class

    ' Immutable definition; loading, rendering and editing use the same finite-value clamp.
    Public NotInheritable Class FilmParameterDefinition
        Public ReadOnly Property Minimum As Single
        Public ReadOnly Property Maximum As Single
        Public ReadOnly Property DefaultValue As Single

        Friend Sub New(minimum As Single, maximum As Single, defaultValue As Single)
            Me.Minimum = minimum
            Me.Maximum = maximum
            Me.DefaultValue = defaultValue
        End Sub

        Public Function ClampValue(value As Double) As Single
            If Not Double.IsFinite(value) Then Return DefaultValue
            Return CSng(Math.Max(Minimum, Math.Min(Maximum, value)))
        End Function

    End Class
End Namespace
