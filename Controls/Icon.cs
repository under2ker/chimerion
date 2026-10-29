using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Dnex.Controls;

/// <summary>
/// Векторная иконка. Анимированные иконки useanimations.com (MIT, https://useanimations.com): в покое стоит один кадр,
/// при наведении на кнопку или пункт меню с иконкой и при нажатии проигрывается анимация (AnimatedIcons). Для
/// остальных контурные иконки Lucide (ISC, https://lucide.dev), логотипы сервисов из Simple Icons (CC0,
/// https://simpleicons.org). Цвет берется у текста вокруг (Foreground наследуется), размер Size.
/// В XAML: &lt;controls:Icon Kind="star" Size="16"/&gt;, в коде: new Icon { Kind = "zap", Size = 18 }.
/// </summary>
public sealed class Icon : Viewbox
{
    /// <summary>Контурные иконки Lucide: поле 24×24, линия 2, скругленные концы.</summary>
    public static readonly IReadOnlyDictionary<string, string> Outline = new Dictionary<string, string>
    {
        ["settings"] = "M 12.22 2 h -.44 a 2 2 0 0 0 -2 2 v .18 a 2 2 0 0 1 -1 1.73 l -.43 .25 a 2 2 0 0 1 -2 0 l -.15 -.08 a 2 2 0 0 0 -2.73 .73 l -.22 .38 a 2 2 0 0 0 .73 2.73 l .15 .1 a 2 2 0 0 1 1 1.72 v .51 a 2 2 0 0 1 -1 1.74 l -.15 .09 a 2 2 0 0 0 -.73 2.73 l .22 .38 a 2 2 0 0 0 2.73 .73 l .15 -.08 a 2 2 0 0 1 2 0 l .43 .25 a 2 2 0 0 1 1 1.73 V 20 a 2 2 0 0 0 2 2 h .44 a 2 2 0 0 0 2 -2 v -.18 a 2 2 0 0 1 1 -1.73 l .43 -.25 a 2 2 0 0 1 2 0 l .15 .08 a 2 2 0 0 0 2.73 -.73 l .22 -.39 a 2 2 0 0 0 -.73 -2.73 l -.15 -.08 a 2 2 0 0 1 -1 -1.74 v -.5 a 2 2 0 0 1 1 -1.74 l .15 -.09 a 2 2 0 0 0 .73 -2.73 l -.22 -.38 a 2 2 0 0 0 -2.73 -.73 l -.15 .08 a 2 2 0 0 1 -2 0 l -.43 -.25 a 2 2 0 0 1 -1 -1.73 V 4 a 2 2 0 0 0 -2 -2 z | M9,12 A3,3 0 1 0 15,12 A3,3 0 1 0 9,12 Z",
        ["arrow-left-right"] = "M 8 3 L 4 7 l 4 4 | M 4 7 h 16 | M 16 21 l 4 -4 l -4 -4 | M 20 17 H 4",
        ["zap"] = "M 4 14 a 1 1 0 0 1 -.78 -1.63 l 9.9 -10.2 a .5 .5 0 0 1 .86 .46 l -1.92 6.02 A 1 1 0 0 0 13 10 h 7 a 1 1 0 0 1 .78 1.63 l -9.9 10.2 a .5 .5 0 0 1 -.86 -.46 l 1.92 -6.02 A 1 1 0 0 0 11 14 z",
        ["stethoscope"] = "M 11 2 v 2 | M 5 2 v 2 | M 5 3 H 4 a 2 2 0 0 0 -2 2 v 4 a 6 6 0 0 0 12 0 V 5 a 2 2 0 0 0 -2 -2 h -1 | M 8 15 a 6 6 0 0 0 12 0 v -3 | M18,10 A2,2 0 1 0 22,10 A2,2 0 1 0 18,10 Z",
        ["eye-off"] = "M 10.733 5.076 a 10.744 10.744 0 0 1 11.205 6.575 a 1 1 0 0 1 0 .696 a 10.747 10.747 0 0 1 -1.444 2.49 | M 14.084 14.158 a 3 3 0 0 1 -4.242 -4.242 | M 17.479 17.499 a 10.75 10.75 0 0 1 -15.417 -5.151 a 1 1 0 0 1 0 -.696 a 10.75 10.75 0 0 1 4.446 -5.143 | M 2 2 l 20 20",
        ["smartphone"] = "M 7 2 h 10 a 2 2 0 0 1 2 2 v 16 a 2 2 0 0 1 -2 2 h -10 a 2 2 0 0 1 -2 -2 v -16 a 2 2 0 0 1 2 -2 z | M 12 18 h 0.01",
        ["circle-help"] = "M 22 12 a 10 10 0 1 1 -20 0 a 10 10 0 1 1 20 0 z | M 9.09 9 a 3 3 0 0 1 5.83 1 c 0 2 -3 3 -3 3 | M 12 17 h 0.01",
        ["info"] = "M2,12 A10,10 0 1 0 22,12 A10,10 0 1 0 2,12 Z | M 12 16 v -4 | M 12 8 h .01",
        ["triangle-alert"] = "M 21.73 18 l -8 -14 a 2 2 0 0 0 -3.48 0 l -8 14 A 2 2 0 0 0 4 21 h 16 a 2 2 0 0 0 1.73 -3 | M 12 9 v 4 | M 12 17 h .01",
        ["circle-check"] = "M2,12 A10,10 0 1 0 22,12 A10,10 0 1 0 2,12 Z | M 9 12 l 2 2 l 4 -4",
        ["circle-x"] = "M2,12 A10,10 0 1 0 22,12 A10,10 0 1 0 2,12 Z | M 15 9 l -6 6 | M 9 9 l 6 6",
        ["download"] = "M 21 15 v 4 a 2 2 0 0 1 -2 2 H 5 a 2 2 0 0 1 -2 -2 v -4 | M7,10 L12,15 L17,10 | M12,15 L12,3",
        ["star"] = "M 11.525 2.295 a .53 .53 0 0 1 .95 0 l 2.31 4.679 a 2.123 2.123 0 0 0 1.595 1.16 l 5.166 .756 a .53 .53 0 0 1 .294 .904 l -3.736 3.638 a 2.123 2.123 0 0 0 -.611 1.878 l .882 5.14 a .53 .53 0 0 1 -.771 .56 l -4.618 -2.428 a 2.122 2.122 0 0 0 -1.973 0 L 6.396 21.01 a .53 .53 0 0 1 -.77 -.56 l .881 -5.139 a 2.122 2.122 0 0 0 -.611 -1.879 L 2.16 9.795 a .53 .53 0 0 1 .294 -.906 l 5.165 -.755 a 2.122 2.122 0 0 0 1.597 -1.16 z",
        ["external-link"] = "M 15 3 h 6 v 6 | M 10 14 L 21 3 | M 18 13 v 6 a 2 2 0 0 1 -2 2 H 5 a 2 2 0 0 1 -2 -2 V 8 a 2 2 0 0 1 2 -2 h 6",
        ["x"] = "M 18 6 L 6 18 | M 6 6 l 12 12",
        ["chevron-down"] = "M 6 9 l 6 6 l 6 -6",
        ["refresh-cw"] = "M 3 12 a 9 9 0 0 1 9 -9 a 9.75 9.75 0 0 1 6.74 2.74 L 21 8 | M 21 3 v 5 h -5 | M 21 12 a 9 9 0 0 1 -9 9 a 9.75 9.75 0 0 1 -6.74 -2.74 L 3 16 | M 8 16 H 3 v 5",
        ["check"] = "M 20 6 L 9 17 l -5 -5",
        ["plus"] = "M 5 12 h 14 | M 12 5 v 14",
        ["scroll-text"] = "M 15 12 h -5 | M 15 8 h -5 | M 19 17 V 5 a 2 2 0 0 0 -2 -2 H 4 | M 8 21 h 12 a 2 2 0 0 0 2 -2 v -1 a 1 1 0 0 0 -1 -1 H 11 a 1 1 0 0 0 -1 1 v 1 a 2 2 0 1 1 -4 0 V 5 a 2 2 0 1 0 -4 0 v 2 a 1 1 0 0 0 1 1 h 3",
        ["sliders-horizontal"] = "M21,4 L14,4 | M10,4 L3,4 | M21,12 L12,12 | M8,12 L3,12 | M21,20 L16,20 | M12,20 L3,20 | M14,2 L14,6 | M8,10 L8,14 | M16,18 L16,22",
        ["shrink"] = "M 15 15 l 6 6 m -6 -6 v 4.8 m 0 -4.8 h 4.8 | M 9 19.8 V 15 m 0 0 H 4.2 M 9 15 l -6 6 | M 15 4.2 V 9 m 0 0 h 4.8 M 15 9 l 6 -6 | M 9 4.2 V 9 m 0 0 H 4.2 M 9 9 L 3 3",
        ["expand"] = "M 21 21 l -6 -6 m 6 6 v -4.8 m 0 4.8 h -4.8 | M 3 16.2 V 21 m 0 0 h 4.8 M 3 21 l 6 -6 | M 21 7.8 V 3 m 0 0 h -4.8 M 21 3 l -6 6 | M 3 7.8 V 3 m 0 0 h 4.8 M 3 3 l 6 6",
        ["send"] = "M 14.536 21.686 a .5 .5 0 0 0 .937 -.024 l 6.5 -19 a .496 .496 0 0 0 -.635 -.635 l -19 6.5 a .5 .5 0 0 0 -.024 .937 l 7.93 3.18 a 2 2 0 0 1 1.112 1.11 z | M 21.854 2.147 l -10.94 10.939",
        ["activity"] = "M 22 12 h -2.48 a 2 2 0 0 0 -1.93 1.46 l -2.35 8.36 a .25 .25 0 0 1 -.48 0 L 9.24 2.18 a .25 .25 0 0 0 -.48 0 l -2.35 8.36 A 2 2 0 0 1 4.49 12 H 2",
        ["shield-check"] = "M 20 13 c 0 5 -3.5 7.5 -7.66 8.95 a 1 1 0 0 1 -.67 -.01 C 7.5 20.5 4 18 4 13 V 6 a 1 1 0 0 1 1 -1 c 2 0 4.5 -1.2 6.24 -2.72 a 1.17 1.17 0 0 1 1.52 0 C 14.51 3.81 17 5 19 5 a 1 1 0 0 1 1 1 z | M 9 12 l 2 2 l 4 -4",
        ["globe"] = "M2,12 A10,10 0 1 0 22,12 A10,10 0 1 0 2,12 Z | M 12 2 a 14.5 14.5 0 0 0 0 20 a 14.5 14.5 0 0 0 0 -20 | M 2 12 h 20",
        ["copy"] = "M10,8 H20 A2,2 0 0 1 22,10 V20 A2,2 0 0 1 20,22 H10 A2,2 0 0 1 8,20 V10 A2,2 0 0 1 10,8 Z | M 4 16 c -1.1 0 -2 -.9 -2 -2 V 4 c 0 -1.1 .9 -2 2 -2 h 10 c 1.1 0 2 .9 2 2",
        ["trash-2"] = "M 3 6 h 18 | M 19 6 v 14 c 0 1 -1 2 -2 2 H 7 c -1 0 -2 -1 -2 -2 V 6 | M 8 6 V 4 c 0 -1 1 -2 2 -2 h 4 c 1 0 2 1 2 2 v 2 | M10,11 L10,17 | M14,11 L14,17",
        ["search"] = "M3,11 A8,8 0 1 0 19,11 A8,8 0 1 0 3,11 Z | M 21 21 l -4.3 -4.3",
        ["link"] = "M 10 13 a 5 5 0 0 0 7.54 .54 l 3 -3 a 5 5 0 0 0 -7.07 -7.07 l -1.72 1.71 | M 14 11 a 5 5 0 0 0 -7.54 -.54 l -3 3 a 5 5 0 0 0 7.07 7.07 l 1.71 -1.71",
        ["minus"] = "M 5 12 h 14",
        ["square"] = "M5,3 H19 A2,2 0 0 1 21,5 V19 A2,2 0 0 1 19,21 H5 A2,2 0 0 1 3,19 V5 A2,2 0 0 1 5,3 Z",
        ["circle-arrow-up"] = "M2,12 A10,10 0 1 0 22,12 A10,10 0 1 0 2,12 Z | M 16 12 l -4 -4 l -4 4 | M 12 16 V 8",
    };

    /// <summary>Логотипы сервисов Simple Icons (заливка, поле 24×24).</summary>
    public static readonly IReadOnlyDictionary<string, string> Filled = new Dictionary<string, string>
    {
        ["openai"] = "M 22.2819 9.8211 a 5.9847 5.9847 0 0 0 -.5157 -4.9108 a 6.0462 6.0462 0 0 0 -6.5098 -2.9 A 6.0651 6.0651 0 0 0 4.9807 4.1818 a 5.9847 5.9847 0 0 0 -3.9977 2.9 a 6.0462 6.0462 0 0 0 .7427 7.0966 a 5.98 5.98 0 0 0 .511 4.9107 a 6.051 6.051 0 0 0 6.5146 2.9001 A 5.9847 5.9847 0 0 0 13.2599 24 a 6.0557 6.0557 0 0 0 5.7718 -4.2058 a 5.9894 5.9894 0 0 0 3.9977 -2.9001 a 6.0557 6.0557 0 0 0 -.7475 -7.0729 z m -9.022 12.6081 a 4.4755 4.4755 0 0 1 -2.8764 -1.0408 l .1419 -.0804 l 4.7783 -2.7582 a .7948 .7948 0 0 0 .3927 -.6813 v -6.7369 l 2.02 1.1686 a .071 .071 0 0 1 .038 .052 v 5.5826 a 4.504 4.504 0 0 1 -4.4945 4.4944 z m -9.6607 -4.1254 a 4.4708 4.4708 0 0 1 -.5346 -3.0137 l .142 .0852 l 4.783 2.7582 a .7712 .7712 0 0 0 .7806 0 l 5.8428 -3.3685 v 2.3324 a .0804 .0804 0 0 1 -.0332 .0615 L 9.74 19.9502 a 4.4992 4.4992 0 0 1 -6.1408 -1.6464 z M 2.3408 7.8956 a 4.485 4.485 0 0 1 2.3655 -1.9728 V 11.6 a .7664 .7664 0 0 0 .3879 .6765 l 5.8144 3.3543 l -2.0201 1.1685 a .0757 .0757 0 0 1 -.071 0 l -4.8303 -2.7865 A 4.504 4.504 0 0 1 2.3408 7.872 z m 16.5963 3.8558 L 13.1038 8.364 L 15.1192 7.2 a .0757 .0757 0 0 1 .071 0 l 4.8303 2.7913 a 4.4944 4.4944 0 0 1 -.6765 8.1042 v -5.6772 a .79 .79 0 0 0 -.407 -.667 z m 2.0107 -3.0231 l -.142 -.0852 l -4.7735 -2.7818 a .7759 .7759 0 0 0 -.7854 0 L 9.409 9.2297 V 6.8974 a .0662 .0662 0 0 1 .0284 -.0615 l 4.8303 -2.7866 a 4.4992 4.4992 0 0 1 6.6802 4.66 z M 8.3065 12.863 l -2.02 -1.1638 a .0804 .0804 0 0 1 -.038 -.0567 V 6.0742 a 4.4992 4.4992 0 0 1 7.3757 -3.4537 l -.142 .0805 L 8.704 5.459 a .7948 .7948 0 0 0 -.3927 .6813 z m 1.0976 -2.3654 l 2.602 -1.4998 l 2.6069 1.4998 v 2.9994 l -2.5974 1.4997 l -2.6067 -1.4997 Z",
        ["claude"] = "M 4.7144 15.9555 l 4.7174 -2.6471 l .079 -.2307 l -.079 -.1275 h -.2307 l -.7893 -.0486 l -2.6956 -.0729 l -2.3375 -.0971 l -2.2646 -.1214 l -.5707 -.1215 l -.5343 -.7042 l .0546 -.3522 l .4797 -.3218 l .686 .0608 l 1.5179 .1032 l 2.2767 .1578 l 1.6514 .0972 l 2.4468 .255 h .3886 l .0546 -.1579 l -.1336 -.0971 l -.1032 -.0972 L 6.973 9.8356 l -2.55 -1.6879 l -1.3356 -.9714 l -.7225 -.4918 l -.3643 -.4614 l -.1578 -1.0078 l .6557 -.7225 l .8803 .0607 l .2246 .0607 l .8925 .686 l 1.9064 1.4754 l 2.4893 1.8336 l .3643 .3035 l .1457 -.1032 l .0182 -.0728 l -.164 -.2733 l -1.3539 -2.4467 l -1.445 -2.4893 l -.6435 -1.032 l -.17 -.6194 c -.0607 -.255 -.1032 -.4674 -.1032 -.7285 L 6.287 .1335 L 6.6997 0 l .9957 .1336 l .419 .3642 l .6192 1.4147 l 1.0018 2.2282 l 1.5543 3.0296 l .4553 .8985 l .2429 .8318 l .091 .255 h .1579 v -.1457 l .1275 -1.706 l .2368 -2.0947 l .2307 -2.6957 l .0789 -.7589 l .3764 -.9107 l .7468 -.4918 l .5828 .2793 l .4797 .686 l -.0668 .4433 l -.2853 1.8517 l -.5586 2.9021 l -.3643 1.9429 h .2125 l .2429 -.2429 l .9835 -1.3053 l 1.6514 -2.0643 l .7286 -.8196 l .85 -.9046 l .5464 -.4311 h 1.0321 l .759 1.1293 l -.34 1.1657 l -1.0625 1.3478 l -.8804 1.1414 l -1.2628 1.7 l -.7893 1.36 l .0729 .1093 l .1882 -.0183 l 2.8535 -.607 l 1.5421 -.2794 l 1.8396 -.3157 l .8318 .3886 l .091 .3946 l -.3278 .8075 l -1.967 .4857 l -2.3072 .4614 l -3.4364 .8136 l -.0425 .0304 l .0486 .0607 l 1.5482 .1457 l .6618 .0364 h 1.621 l 3.0175 .2247 l .7892 .522 l .4736 .6376 l -.079 .4857 l -1.2142 .6193 l -1.6393 -.3886 l -3.825 -.9107 l -1.3113 -.3279 h -.1822 v .1093 l 1.0929 1.0686 l 2.0035 1.8092 l 2.5075 2.3314 l .1275 .5768 l -.3218 .4554 l -.34 -.0486 l -2.2039 -1.6575 l -.85 -.7468 l -1.9246 -1.621 h -.1275 v .17 l .4432 .6496 l 2.3436 3.5214 l .1214 1.0807 l -.17 .3521 l -.6071 .2125 l -.6679 -.1214 l -1.3721 -1.9246 L 14.38 17.959 l -1.1414 -1.9428 l -.1397 .079 l -.674 7.2552 l -.3156 .3703 l -.7286 .2793 l -.6071 -.4614 l -.3218 -.7468 l .3218 -1.4753 l .3886 -1.9246 l .3157 -1.53 l .2853 -1.9004 l .17 -.6314 l -.0121 -.0425 l -.1397 .0182 l -1.4328 1.9672 l -2.1796 2.9446 l -1.7243 1.8456 l -.4128 .164 l -.7164 -.3704 l .0667 -.6618 l .4008 -.5889 l 2.386 -3.0357 l 1.4389 -1.882 l .929 -1.0868 l -.0062 -.1579 h -.0546 l -6.3385 4.1164 l -1.1293 .1457 l -.4857 -.4554 l .0608 -.7467 l .2307 -.2429 l 1.9064 -1.3114 Z",
        ["gemini"] = "M 11.04 19.32 Q 12 21.51 12 24 q 0 -2.49 .93 -4.68 q .96 -2.19 2.58 -3.81 t 3.81 -2.55 Q 21.51 12 24 12 q -2.49 0 -4.68 -.93 a 12.3 12.3 0 0 1 -3.81 -2.58 a 12.3 12.3 0 0 1 -2.58 -3.81 Q 12 2.49 12 0 q 0 2.49 -.96 4.68 q -.93 2.19 -2.55 3.81 a 12.3 12.3 0 0 1 -3.81 2.58 Q 2.49 12 0 12 q 2.49 0 4.68 .96 q 2.19 .93 3.81 2.55 t 2.55 3.81",
    };

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(Icon), new PropertyMetadata("", (d, _) => ((Icon)d).Build()));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Icon), new PropertyMetadata(16.0, (d, e) => { var i = (Icon)d; i.Width = i.Height = (double)e.NewValue; }));
    /// <summary>Залить контур (например, звезда избранного).</summary>
    public static readonly DependencyProperty SolidProperty = DependencyProperty.Register(
        nameof(Solid), typeof(bool), typeof(Icon), new PropertyMetadata(false, (d, _) => ((Icon)d).Build()));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(Icon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
        nameof(StrokeWidth), typeof(double), typeof(Icon), new PropertyMetadata(2.0, (d, _) => ((Icon)d).Build()));

    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public bool Solid { get => (bool)GetValue(SolidProperty); set => SetValue(SolidProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

    public Icon()
    {
        Width = Height = 16;
        Stretch = Stretch.Uniform;
        SnapsToDevicePixels = true;
        VerticalAlignment = VerticalAlignment.Center;
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { Detach(); Stop(); };
    }

    // ---------- Анимация ----------

    private AnimatedGlyph? _glyph;
    private UIElement? _trigger;
    private System.Diagnostics.Stopwatch? _clock;

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ForegroundProperty) _glyph?.InvalidateVisual(); // цвет темы сменился: кадр перерисовывается
    }

    /// <summary>Что запускает анимацию: ближайшая кнопка, пункт меню или вкладка вокруг иконки, иначе сама иконка.</summary>
    private UIElement FindTrigger()
    {
        DependencyObject? d = this;
        for (int i = 0; i < 8 && d != null; i++)
        {
            // Иконка внутри строки текста (InlineUIContainer) лежит в элементе без визуального дерева: у него
            // VisualTreeHelper бросает исключение, поэтому для таких берем логического родителя
            d = (d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d);
            if (d is System.Windows.Controls.Primitives.ButtonBase or MenuItem or ListBoxItem) return (UIElement)d;
        }
        return this;
    }

    private void Attach()
    {
        Detach();
        if (_glyph == null) return;
        _trigger = FindTrigger();
        _trigger.MouseEnter += OnEnter;
        _trigger.PreviewMouseLeftButtonDown += OnPress;
    }

    private void Detach()
    {
        if (_trigger == null) return;
        _trigger.MouseEnter -= OnEnter;
        _trigger.PreviewMouseLeftButtonDown -= OnPress;
        _trigger = null;
    }

    private void OnEnter(object sender, System.Windows.Input.MouseEventArgs e) { if (_clock == null) Play(); }
    private void OnPress(object sender, System.Windows.Input.MouseButtonEventArgs e) => Play();

    /// <summary>Проиграть анимацию один раз с начала. Если в Windows выключены анимации, иконка не двигается.</summary>
    public void Play()
    {
        if (_glyph == null || !SystemParameters.ClientAreaAnimation) return;
        if (_clock == null) CompositionTarget.Rendering += OnFrame;
        _clock = System.Diagnostics.Stopwatch.StartNew();
        _glyph.Frame = 0;
    }

    private void Stop()
    {
        if (_clock == null) return;
        CompositionTarget.Rendering -= OnFrame;
        _clock = null;
        if (_glyph != null) _glyph.Frame = _glyph.Anim.RestFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_glyph == null || _clock == null) { Stop(); return; }
        int frame = (int)(_clock.Elapsed.TotalSeconds * _glyph.Anim.Fps);
        if (frame >= _glyph.Anim.Frames.Length) { Stop(); return; }
        _glyph.Frame = frame;
    }

    /// <summary>Контуры через «|»: каждый своей фигурой, как отдельные элементы в исходном SVG.</summary>
    private static Geometry Parse(string data)
    {
        var parts = data.Split('|');
        if (parts.Length == 1) return Geometry.Parse(data);
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var p in parts) g.Children.Add(Geometry.Parse(p.Trim()));
        return g;
    }

    private void Build()
    {
        Stop();
        _glyph = null;
        // Анимированная версия, если она есть (кроме залитых: залитая звезда избранного остается статичной)
        if (!Solid && AnimatedIcons.Get(Kind) is { } anim)
        {
            _glyph = new AnimatedGlyph(this, anim);
            // Рисунок занимает середину поля 32×32: показываем центральные 24×24, тогда размер и толщина линии как у Lucide
            double inset = (anim.Size - 24) / 2;
            Canvas.SetLeft(_glyph, -inset);
            Canvas.SetTop(_glyph, -inset);
            Child = new Canvas { Width = 24, Height = 24, ClipToBounds = true, Children = { _glyph } };
            if (IsLoaded) Attach();
            return;
        }
        if (IsLoaded) Detach();
        var canvas = new Canvas { Width = 24, Height = 24 };
        var path = new Path();
        var color = new Binding(nameof(Foreground)) { Source = this };
        if (Filled.TryGetValue(Kind, out var logo))
        {
            path.Data = Parse(logo);
            path.SetBinding(Shape.FillProperty, color);
        }
        else if (Outline.TryGetValue(Kind, out var d))
        {
            path.Data = Parse(d);
            path.SetBinding(Shape.StrokeProperty, color);
            if (Solid) path.SetBinding(Shape.FillProperty, color);
            path.StrokeThickness = StrokeWidth;
            path.StrokeStartLineCap = path.StrokeEndLineCap = PenLineCap.Round;
            path.StrokeLineJoin = PenLineJoin.Round;
        }
        canvas.Children.Add(path);
        Child = canvas;
    }
}
