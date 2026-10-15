#!/bin/sh
# ONNX Telemetrie wird über den Parameter abgeschaltet
ORT_DISABLE_TELEMETRY=1
export ORT_DISABLE_TELEMETRY
exec /app/lib/ferrumpix/FerrumPix "$@"
