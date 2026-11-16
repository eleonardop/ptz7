# Etapa 1 — Correções graves (resumo)

Itens corrigidos (auditoria C1–C4, A1–A8):
- Guardião independente da tela: se o ciclo de controle travar com a câmera em movimento, ela é parada em ~0,5 s.
- Parada de segurança no encerramento inesperado do programa e reenvio da parada quando a câmera volta a responder.
- Parada repetida por ~5,5 s depois de soltar o analógico (pacote UDP perdido).
- Reconexão: o soquete de rede é recriado após falhas, mudança de rede ou erro de envio; botão "Reconectar" na faixa de alerta.
- Acionar preset e Home confirmados pela câmera (ACK), com reenvio limitado.
- Home e Salvar preset exigem segurar o botão por 0,5 s.
- Detecção de botão/eixo termina sozinha em 10 s e não silencia mais o controle sem aviso.
- Mapeamento atrelado ao controle (VID:PID); controle diferente suspende as funções de botão e avisa.
- Leitura segura das configurações (nova tentativa, .bak, arquivo preservado, gravação atômica).
- Mouse sempre pode retomar o comando; faixa de alerta fixa; barra de presets fixa; decodificação por placa de vídeo desligada por padrão.

Não feito nesta etapa (fica para depois): thread dedicada de controle, vídeo em processo separado, confirmação de troca de perfil, reinício da sequência VISCA over IP.

IMPORTANTE: nada foi compilado nem executado no ambiente em que isto foi escrito (sem .NET). A compilação e os testes automatizados rodam no GitHub Actions. Dependem da câmera e do GameSir reais: tempos de resposta, ACK da câmera LJ510-30X-N, troca USB/Bluetooth do controle, queda e volta da rede, vídeo RTSP.
