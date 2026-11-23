# Guion de verificación física — pendiente de humano (QA _t_)

> Lo que no se puede automatizar en wp-env/xUnit. Para quien ejecute las pruebas
> de H1 (Doc/05 §4.3-4.4, F2 completo). Marcar cada casilla con fecha e impresora.

## 1. Codificación real (Doc/05 §4.3)

- [ ] Conectar Epson TM-T20III (USB, 80 mm) y ejecutar `orvitaprint test`.
- [ ] En pantalla/impresión debe salir, sin errores:
      `Pizza Margarita · jamón, piña, ñ, €, áéíóú`
      `C/ Cañada 3, 2º B - 28801 Alcalá`
- [ ] Repetir con encoding CP850 (el € sale como «E» y la raya como «-»: es la
      sustitución documentada, con aviso en el log del agente).
- [ ] Repetir con CP858 (el € debe salir ya como €).
- [ ] Repetir con CP437 (como CP850: € → «E»).
- [ ] Verificar en `orvitaprint status`/log que cada sustitución queda registrada.
- [ ] Repetir en Xprinter XP-80C y en la genérica Ethernet 9100.

## 2. Corte, cajón, copias

- [ ] `cut_mode=partial` corta parcial; `full` corta completo (GS V 0/1).
- [ ] Con cajón conectado y `open_drawer=1`, el cajón se abre al imprimir.
- [ ] Una salida con `copies=2` imprime dos veces el mismo ticket con UN trabajo.

## 3. Sesión 0 y arranque

- [ ] El servicio imprime con la sesión bloqueada/desconectada (psexec -s o reinicio).
- [ ] Corte de luz durante la impresión: al arrancar reimprime y el trabajo queda
      marcado `duplicate_suspected` (ver `orvitaprint status`; Doc/03 §6.4).
- [ ] `PRAGMA journal_mode=WAL` sobrevive al corte: la BD no se corrompe.

## 4. Panel (respuesta visual)

- [ ] Un pedido sin regla coincidente muestra el aviso amarillo en wp-admin
      («los pedidos X no coinciden…»). El mecanismo (transient) ya se verificó
      dinámicamente; falta ver el HTML en el navegador.
- [ ] La divergencia local por defecto (hallazgo F1): con «Ninguno» elegido en
      Ajustes, comprobar qué muestra Locales y confirmar el aviso/etiqueta.

## 5. Registro de ejecución

| Fecha | Impresora | Codificación | Resultado | Notas |
|---|---|---|---|---|
| | | | | |
