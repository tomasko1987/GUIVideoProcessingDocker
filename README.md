# GUIVideoProcessing.Web

ASP.NET Core web application for processing an MJPEG video stream from an ESP32-CAM camera. It recognises digits on a 7-segment display using either an ONNX neural network or an algorithmic SevenSegment decoder. Results are displayed in real time via SignalR and written to InfluxDB.

## Technologies

- **ASP.NET Core 9** — web framework
- **OpenCvSharp4** — image processing (filter, threshold, morphology)
- **ONNX Runtime** — neural network inference
- **SignalR** — real-time push notifications to the browser
- **InfluxDB** — time-series storage of recognised values
- **Serilog** — logging to console and files
- **Docker / Ubuntu 22.04** — deployment

## Documentation

Detailed documentation is located in the [`docs/`](docs/) folder:

- [`docs/dokumentacia.html`](docs/dokumentacia.html) — deployment, Dockerfile, appsettings.json, logs, Code Update
- [`docs/architektura.html`](docs/architektura.html) — code architecture, description of all classes, pipeline, GUI, API endpoints

## Screenshots

### Algorithm
![Algorithm](docs/Algoritmus.png)

### Graph
![Graph](docs/Graf.png)

### Bilateral
![Bilateral](docs/Bilateral.png)

### CLAHE
![CLAHE](docs/CLAHE.png)

### Threshold
![Threshold](docs/Threshold.png)

### Morphology
![Morphology](docs/Morphology.png)

### Split
![Split](docs/Split.png)

### InfluxDB
![InfluxDB](docs/InfluxDB.png)
