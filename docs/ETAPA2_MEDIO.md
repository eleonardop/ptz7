# Etapa 2 — Correções médias (resumo)

Itens da auditoria M1–M17 e o botão de autofoco:
- Consultas à câmera só aceitam a resposta do formato certo (e, no VISCA over IP, o mesmo número de sequência).
- Queda detectada em ~7 s (antes ~20 s). Reconexão ajustável de 1 a 3 verificações.
- Inversão do analógico passa por zero (sem tranco). Preset/Home ganham do analógico até ele voltar ao centro; folga normal do analógico não trava mais a parada de segurança.
- Autofoco: botão na tela com o estado REAL lido da câmera, foco de 1 toque, foco perto/longe, e duas funções mapeáveis no controle.
- Preset: limite configurável por câmera e aviso quando a câmera recusa.
- Relógio monotônico (mudança de hora do Windows não afeta intervalos); suspensão/retomada do Windows tratada.
- Tela cheia escondida não reconecta o vídeo; opção de baixa latência.
- Backup/exportação com opção de ficar sem a senha RTSP.
- Erros inesperados na faixa de alerta, sem janela modal.
- VISCA over IP: reinício da sequência ao conectar.
- Procurar câmera na rede (ONVIF) e recomendação de IP fixo.
- Indicador de alterações de IP/porta/protocolo ainda não aplicadas.
- Repouso dos gatilhos: só vale depois de 0,3 s estável, e salto grande é rejeitado com aviso (Recalibrar repouso).

Não feito: vídeo como segundo sinal de “câmera viva” (M3 parcial); atalhos de teclado, foco visível, “Modo ao vivo”, iniciar com o Windows (etapa 3).

IMPORTANTE: nada foi compilado nem executado aqui (sem .NET). A compilação e os testes rodam no GitHub Actions. Dependem da câmera e do GameSir reais: suporte da LJ510-30X-N à consulta de foco e ao foco de 1 toque, reinício de sequência do VISCA over IP, descoberta ONVIF, retomada após suspensão, comportamento dos gatilhos, latência real do vídeo.
