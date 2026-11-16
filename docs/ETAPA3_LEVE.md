# Etapa 3 — Leves e gatilhos analógicos (resumo)

**Pedido especial: RT/LT como zoom proporcional**
- RT/LT mapeados em Zoom aproximar/afastar usam a pressão analógica do gatilho; a velocidade do zoom varia com a pressão.
- Soltar o gatilho envia a parada de zoom no mesmo ciclo, sem rampa (mesmo com “Parada suave” ligada).
- Vale para gatilhos em eixos separados e para LT+RT num só eixo (a pressão é medida sobre o curso disponível). Sem leitura analógica, o gatilho cai no comportamento antigo de botão.
- Botões comuns mapeados em zoom não mudam.

**Itens leves da auditoria**
- B1: pincéis da mistura de cor dos gatilhos na tela são reaproveitados (16 degraus).
- B3: anel de foco visível (azul tracejado) em botões e controles; dá para ver onde está o foco ao usar Tab.
- B4: avisos e erros (cores âmbar e vermelha) e as mensagens de estado têm no mínimo 14 px.
- B5: valores antigos de ações removidas continuam marcados como “REMOVIDA”/só leitura no código; o manual não cita mais nenhum deles.
- B6: campos RTSP nulos em arquivo editado à mão são normalizados (a senha nunca é alterada).
- B7: a sondagem de porta da câmera inclui a porta já digitada na aba Câmera; a sondagem RTSP usa a porta do endereço RTSP digitado.
- B8: instância única valendo entre sessões do Windows; se a outra cópia estiver em outra sessão, o programa avisa.

**Não feito**
- B2 (reduzir alocações a cada ciclo de 16 ms): a auditoria pedia medir antes; não há sintoma conhecido, então não foi mexido.
- Atalhos de teclado, “Modo ao vivo” e iniciar com o Windows: não estavam no pedido desta etapa.

IMPORTANTE: nada foi compilado nem executado aqui (sem .NET); compilação e testes rodam no GitHub Actions. Depende do GameSir real: como o Windows entrega RT/LT (eixo separado, eixo combinado ou só botão), o curso real de cada gatilho e se a leitura de repouso é estável. Confirme com DETECTAR BOTÃO no gatilho e veja a barra de pressão no desenho do controle (aba Mapeamento).
