---
description: Comenzar (o continuar) la implementación del plan. Delega en los agentes según Doc/07.
agent: orquestador
---

Comienza o continúa la implementación del plan de Orvita Print.

Documento de planificación: `$ARGUMENTS` (si no se ha indicado ninguno, usa `Doc/07-plan-tecnico-y-tareas.md`).

Procede así:

1. **Lee el documento** completo: tareas, entregables, dependencias (§5) y hitos. Identifica el estado actual: ¿hay tareas hechas? ¿dónde quedó la última ronda?
2. **Selecciona el siguiente lote**: las tareas pendientes cuyas dependencias ya están satisfechas, agrupadas en un entregable verificable (no más de 3-4 tareas). Si hay trabajo paralelizable entre el plugin (PHP) y el agente (.NET), láncalo en paralelo.
3. **Delega**: `plugin-dev` para `F0-*`/`F1-*`, `csharp-dev` para `F2-*`/`F3-*`, pasando a cada agente el ID de tarea, su entregable y las secciones exactas de los docs que debe leer.
4. **Verifica con `tester`** el lote contra la definición de hecho de la fase (`Doc/06` §10 para F0, `Doc/05` §3 para el resto). Lista lo que requiera verificación física (impresora, TPV) como pendiente explícito, no como hecho.
5. **Actualiza el tablero** en el documento de planificación: tareas marcadas como hechas con fecha real, desviaciones de estimación, y tareas desbloqueadas.
6. **Reporta**: estado del tablero, hallazgos del tester, y el siguiente lote propuesto.

Reglas: el contrato de `Doc/04` está congelado; lo que no esté en el plan va al backlog de `Doc/05` §8; si algo requiere una decisión del socio, pregunta en lugar de improvisar.
