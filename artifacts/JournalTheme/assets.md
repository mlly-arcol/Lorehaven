# Journal theme artwork

Generated with the built-in imagegen tool from the user's two approved references.
The user's source images were read without being overwritten.

## Production assets

| Asset | Dimensions | Purpose |
| --- | --- | --- |
| `Assets/Journal/paper-texture.png` | 1024 x 1024 | Fine ivory paper surface, no objects or lettering |
| `Assets/Journal/harbor-header.png` | 1536 x 640 | Watercolor sunset harbor and lighthouse, pale left half for live title text |
| `Assets/Journal/notes-decoration.png` | 768 x 768 | Book, nautical chart, compass, lantern and lighthouse postcard; upper space kept clear |
| `Assets/Journal/sidebar-decoration.png` | 768 x 768 | Same illustration reused for coherent decorative corners |
| `Assets/NativeTavern.png` | 1024 x 1024 RGBA | Approved lighthouse/book motif extracted onto a transparent exterior |
| `Assets/NativeTavern.ico` | 16, 24, 32, 48, 64, 128, 256 | Windows executable icon frames |

## Prompt set

- **Header:** Professional digital watercolor / gouache on warm ivory book paper; wide 1536:640 composition. Right half shows a warmly lit coastal harbor at sunset, lighthouse, houses and wooden jetty, lavender-orange clouds. Left half smoothly fades to almost empty cream paper so the application title remains live text. No UI, text, faces, frame or watermark.
- **Paper:** Close uniform flat scan of premium ivory handmade paper near `#F8F4EB`, extremely delicate fibers, uniform brightness and edge matching for repeat use. No stains, folds, objects, letters, prominent specks, shadows or border.
- **Notes decoration:** Warm ivory story journal watercolor, upper 45 percent clear. Lower right arrangement of a forest green antique journal, brass compass, nautical chart, amber lantern, olive leaves and lighthouse postcard. No UI, lettering, faces or watermark.
- **Icon:** Preserve the approved cream rounded-square tile, forest green lighthouse rising from an ivory/green open book, amber halo and lantern, and small terracotta spine. Remove surrounding presentation background and large shadow; generate transparent alpha outside the tile. Refinement requested removal of stray exterior fragments without changing the interior illustration.

## Packaging and validation

`prepare-assets.py` only resizes generated images with Lanczos and encodes PNG/ICO.
It does not draw artwork, infer transparency, or apply an image mask.
`asset-validation.json` records dimensions, image modes, alpha range and ICO frames.
The icon has a real alpha channel (0 to 255); the generated cutout retains a few very faint edge fragments visible when inspected enlarged, so no claim of mathematically perfect exterior transparency is made.

