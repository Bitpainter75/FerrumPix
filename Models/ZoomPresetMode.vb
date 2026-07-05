Namespace Models

    ''' <summary>
    ''' Welcher Zoom-Modus zuletzt bewusst gewählt wurde - von Viewer und Editor gemeinsam genutzt,
    ''' damit Fit/100%-Buttons als aktiv markiert werden können und der gewählte Modus über einen
    ''' Bildwechsel hinweg erhalten bleibt (nur eine manuelle Zoomänderung setzt ihn auf Manual zurück).
    ''' </summary>
    Public Enum ZoomPresetMode
        Fit
        Actual
        Manual
    End Enum

End Namespace
