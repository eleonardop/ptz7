# Vídeo ao vivo no Controle PTZ

## Onde fica
Na aba **Controle PTZ**, no cartão **VÍDEO AO VIVO**, no topo da coluna da direita. Os demais cartões continuam onde estavam; o antigo cartão "Entrada ativa" virou o cartão informativo "Entradas" (o controle físico e o joystick virtual agora funcionam ao mesmo tempo) e fica no fim da coluna. Os presets rápidos saíram deste cartão e agora ficam na barra fixa de presets, abaixo do cabeçalho da janela, visível em todas as abas; o botão Home passou a pedir que seja segurado por meio segundo. Não existe aba nova.

## Como usar
1. Clique em **Ajustes** (no cartão) e informe, se necessário, **usuário** e **senha** da câmera.
2. Clique em **Descobrir (ONVIF)**. O programa pergunta à câmera (no IP cadastrado na aba Câmera) quais fluxos de vídeo ela tem e preenche o endereço **principal** e, se existir, o da **prévia** (stream secundário).
   - Se a câmera não tiver ONVIF, use **Testar endereços comuns** ou digite o endereço RTSP do manual da câmera, no formato `rtsp://192.168.1.100/1`.
3. Clique em **Ligar**. Duplo clique no vídeo (ou o botão **Tela cheia**) abre em tela cheia; **Esc**, **F11** ou duplo clique fecham.

## Como funciona
- **Prévia leve:** o cartão usa o stream secundário (menor resolução) quando existir; caso contrário, reduz a imagem do principal para no máximo 640×360 antes de entregá-la à tela. Em **Ajustes > Qualidade da prévia** dá para escolher o principal.
- **Tela cheia:** usa o stream principal (se houver), limitado a 1280×720.
- **H.264 e H.265:** a decodificação é feita pelo LibVLC. Por padrão **sem** aceleração pela placa de vídeo (é mais estável em drivers antigos e no Windows 7); em Ajustes há a opção "Usar decodificação pela placa de vídeo", que reduz a CPU mas pode ser menos estável. Se um formato não puder ser decodificado no computador, o cartão mostra "Sem imagem da câmera".
- **Sem som:** o áudio da câmera nunca toca no computador.
- **RTSP sobre TCP:** evita perda de pacotes e não conflita com os comandos VISCA (UDP).
- **Não atrapalha os comandos:** abrir, parar e reconectar rodam em segundo plano; a decodificação roda nas threads do LibVLC; a tela só copia um quadro por vez (cerca de 25 por segundo) com prioridade menor que a do ciclo de comandos.
- **Para sozinho:** o vídeo para quando a aba Controle PTZ não está visível (outra aba, janela minimizada ou na bandeja) e volta ao reabrir. Se a conexão cair, o programa tenta reconectar sozinho, com intervalos crescentes.
- **Padrão:** vídeo desligado em cada câmera; sua escolha (ligado/desligado) é lembrada por câmera.

## Observações
- A senha RTSP fica no arquivo de configurações em texto simples, como o resto, e vai junto em backups e exportações.
- Muitas câmeras aceitam poucas conexões RTSP ao mesmo tempo (o OBS também conta). Se o vídeo não abrir com o OBS ligado, desligue o vídeo do programa ou use o stream secundário.
- O componente de vídeo fica na pasta `libvlc`, ao lado do `PTZStudio.exe` (o instalador já a copia). Se você usar o `.exe` solto, copie também essa pasta; sem ela o programa abre normalmente e só o vídeo fica indisponível, com aviso na tela.
- O programa passou a ser compilado só para 64 bits (o componente de vídeo não é distribuído para 32 bits).

## O que NÃO foi testado (depende da câmera, do PC e do Windows)
- Se a LJ510-30X-N tem ONVIF, qual é o endereço RTSP dela e se oferece stream secundário.
- Reprodução real (H.264/H.265), atraso, uso de CPU e memória, tela cheia e reconexão, principalmente no Windows 7 (o .NET 8 não é oficialmente suportado nele).
- Se a pasta `libvlc` entra na publicação do GitHub Actions como esperado (o workflow avisa se não entrar).

## Testes automatizados (rodam no GitHub Actions)
`VideoAndOnvifTests`: validação e senha dos endereços RTSP, configurações antigas sem os campos novos, leitura ONVIF com uma câmera simulada (perfis, endereços, escolha de principal e prévia, login só quando há senha), cabeçalho de segurança, falhas, e teste de caminhos RTSP em loopback.


## Baixa latência (etapa 2)
No cartão de vídeo, a opção **Baixa latência** reduz o armazenamento do vídeo de 300 ms para 120 ms: a imagem chega mais rápido, mas pode picotar em rede ruim. Com a tela cheia escondida ou minimizada o vídeo não é mais reconectado em segundo plano.
