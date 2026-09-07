# Third-party assets

## Zerove font

- File: `AmiGotekMediaBuilder.Gui/Assets/Fonts/Zerove-Regular.ttf`
- Author: GGBotNet
- License: Creative Commons Zero v1.0 Universal (CC0)
- Source: https://github.com/ggbotnet/fonts-cc0/tree/main/Zerove
- License text: https://creativecommons.org/publicdomain/zero/1.0/
- SHA-256: `4B52F5161852CCD55F3649206E23966D78E7DD864270991DAD43547099CF5DF9`

The regular font is bundled unchanged and is used only by the main
`AmiGotekMediaBuilder.Gui` application. The demoscene application remains a
separate program and does not depend on this asset.

## MatrixType Display font

- File: `AmiGotekMediaBuilder.Demoscene.Gui/Assets/Fonts/MatrixTypeDisplay-Regular.ttf`
- Author: GGBotNet
- License: Creative Commons Zero v1.0 Universal (CC0)
- Source: https://github.com/ggbotnet/fonts-cc0/tree/main/MatrixType
- License text: https://creativecommons.org/publicdomain/zero/1.0/
- SHA-256: `36525C6962A7CDF11CF62C95A839FFB840E341EDE9905DDF2D6C757208635A95`

The font is bundled unchanged and is used only by the separate
`AmiGotekMediaBuilder.Demoscene.Gui` application.

## Default game artwork

- File: `AmiGotekMediaBuilder.Core/Assets/default-game-artwork.jpg`
- License: Creative Commons Zero v1.0 Universal (CC0)
- SHA-256: `6B193A833F45488A1A490118C721A4677FA1C19260154ACEE33CD5F485226EC3`

This small blue floppy-disk image is the built-in fallback thumbnail for
ordinary games when no provider returns artwork. It is never used for the
separate demoscene pipeline and can be replaced by a downstream distribution.
