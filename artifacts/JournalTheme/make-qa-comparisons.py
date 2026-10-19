"""Arrange unmodified source/capture regions for visual review; never writes app assets."""
from pathlib import Path
from PIL import Image, ImageDraw

base = Path(__file__).resolve().parent
reference = Image.open(base / 'Reference/theme.png').convert('RGB')
actual = Image.open(base / 'Preview/shell-chat-light-1487x1058.png').convert('RGB')

def compare(name, left, right):
    board = Image.new('RGB', (left.width + right.width + 24, max(left.height, right.height) + 32), '#E7E2D7')
    ImageDraw.Draw(board).text((8, 8), 'Selected reference', fill='#244D47')
    ImageDraw.Draw(board).text((left.width + 32, 8), 'Rendered native application', fill='#244D47')
    board.paste(left, (0, 32))
    board.paste(right, (left.width + 24, 32))
    board.save(base / name)

compare('comparison-full.png', reference, actual)
compare('comparison-conversation.png', reference.crop((274, 330, 1180, 840)), actual.crop((248, 275, 1208, 720)))
compare('comparison-header-notes.png', reference.crop((274, 0, 1487, 340)), actual.crop((248, 0, 1487, 340)))
icon_reference = Image.open(base / 'Reference/icon.png').convert('RGBA').crop((155, 165, 1100, 1090))
icon_actual = Image.open(base.parents[1] / 'Assets/NativeTavern.png').convert('RGBA')
icon_normalized = Image.new('RGBA', (945, 925), '#F5F1E8')
icon_actual.thumbnail((900, 900), Image.Resampling.LANCZOS)
icon_normalized.alpha_composite(icon_actual, ((945-icon_actual.width)//2, (925-icon_actual.height)//2))
compare('comparison-icon.png', icon_reference.convert('RGB'), icon_normalized.convert('RGB'))
