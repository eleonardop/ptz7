# Rodada 5 — nova tela Controle PTZ

## O que mudou
- **Vídeo ao vivo à esquerda, grande** (16:9, acompanha o tamanho da janela). Botões Ligar/Desligar, Tela cheia e Ajustes embaixo. Os campos de RTSP só aparecem ao clicar em Ajustes (já era assim; agora o texto de ajuda virou dica ao parar o mouse).
- **Presets e Home embaixo do vídeo**, em botões de mesma largura. A barra de presets do topo da janela foi removida (os presets ficam só na aba Controle PTZ).
- **Coluna da direita:** Velocidade Pan/Tilt e Intensidade do zoom no topo; joystick virtual; ao lado dele Zoom, Foco e Brilho/Exposição em botões compactos.
- **Joystick virtual com 8 setas de segurar:** a câmera anda enquanto a seta está apertada e para ao soltar, ao sair de cima da seta com o botão apertado, ao trocar de janela ou de aba. Velocidade = Velocidade Pan/Tilt do perfil. O arraste do botão azul continua como antes, começando no círculo do centro.
- **Ajuste automático à altura da tela:** o vídeo é limitado para os presets continuarem visíveis; o cartão Brilho/Exposição se recolhe sozinho se a coluna não couber (a seta do título abre/fecha à mão).
- **Janela estreita:** os controles descem para baixo do vídeo.
- **Prévia maior:** com o vídeo grande na tela, a prévia é decodificada em até 960×540 (antes sempre 640×360).
- **Diagnóstico:** nova linha "Ciclo de controle", com o maior intervalo entre dois ciclos (recente e desde a abertura), para ver se o vídeo atrasa os comandos.

## Nada foi removido
Todas as funções da tela antiga continuam: velocidade por perfil, intensidade do zoom, zoom de segurar, autofoco com estado lido da câmera, foco de um toque, foco perto/longe, brilho e exposição (com − / + / voltar ao valor inicial), Detectar, Home de segurar, "Enviado agora", "No comando agora", avisos de parada de segurança e de viagem a preset. Textos longos de explicação viraram dicas (parar o mouse sobre o controle).

## O que NÃO pôde ser conferido aqui
- A **aparência** da tela: o programa só roda no Windows. O código compila no GitHub Actions, mas espaçamentos, tamanhos e se tudo cabe sem rolar na tela do notebook só dá para ver com uma captura de tela.
- As **setas de direção** com a câmera real (sentido, velocidade, parada ao soltar).
- O efeito do **vídeo maior** no tempo de resposta dos comandos (ver Diagnóstico > Ciclo de controle com o vídeo ligado).
